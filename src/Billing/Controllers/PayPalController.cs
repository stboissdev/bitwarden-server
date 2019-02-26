﻿using Bit.Billing.Utilities;
using Bit.Core.Enums;
using Bit.Core.Models.Table;
using Bit.Core.Repositories;
using Bit.Core.Services;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Newtonsoft.Json;
using System.Data.SqlClient;
using System.IO;
using System.Text;
using System.Threading.Tasks;

namespace Bit.Billing.Controllers
{
    [Route("paypal")]
    public class PayPalController : Controller
    {
        private readonly BillingSettings _billingSettings;
        private readonly PayPalClient _paypalClient;
        private readonly PayPalIpnClient _paypalIpnClient;
        private readonly ITransactionRepository _transactionRepository;
        private readonly IOrganizationRepository _organizationRepository;
        private readonly IUserRepository _userRepository;
        private readonly IMailService _mailService;
        private readonly IPaymentService _paymentService;
        private readonly ILogger<PayPalController> _logger;

        public PayPalController(
            IOptions<BillingSettings> billingSettings,
            PayPalClient paypalClient,
            PayPalIpnClient paypalIpnClient,
            ITransactionRepository transactionRepository,
            IOrganizationRepository organizationRepository,
            IUserRepository userRepository,
            IMailService mailService,
            IPaymentService paymentService,
            ILogger<PayPalController> logger)
        {
            _billingSettings = billingSettings?.Value;
            _paypalClient = paypalClient;
            _paypalIpnClient = paypalIpnClient;
            _transactionRepository = transactionRepository;
            _organizationRepository = organizationRepository;
            _userRepository = userRepository;
            _mailService = mailService;
            _paymentService = paymentService;
            _logger = logger;
        }

        [HttpPost("webhook")]
        public async Task<IActionResult> PostWebhook([FromQuery] string key)
        {
            if(key != _billingSettings.PayPal.WebhookKey)
            {
                return new BadRequestResult();
            }

            if(HttpContext?.Request == null)
            {
                return new BadRequestResult();
            }

            string body = null;
            using(var reader = new StreamReader(HttpContext.Request.Body, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            if(string.IsNullOrWhiteSpace(body))
            {
                return new BadRequestResult();
            }

            var verified = await _paypalClient.VerifyWebhookAsync(body, HttpContext.Request.Headers,
                _billingSettings.PayPal.WebhookId);
            if(!verified)
            {
                return new BadRequestResult();
            }

            if(body.Contains("\"PAYMENT.SALE.COMPLETED\""))
            {
                var ev = JsonConvert.DeserializeObject<PayPalClient.Event<PayPalClient.Sale>>(body);
                var sale = ev.Resource;
                var saleTransaction = await _transactionRepository.GetByGatewayIdAsync(
                    GatewayType.PayPal, sale.Id);
                if(saleTransaction == null)
                {
                    var ids = sale.GetIdsFromCustom();
                    if(ids.Item1.HasValue || ids.Item2.HasValue)
                    {
                        try
                        {
                            await _transactionRepository.CreateAsync(new Core.Models.Table.Transaction
                            {
                                Amount = sale.Amount.TotalAmount,
                                CreationDate = sale.CreateTime,
                                OrganizationId = ids.Item1,
                                UserId = ids.Item2,
                                Type = TransactionType.Charge,
                                Gateway = GatewayType.PayPal,
                                GatewayId = sale.Id,
                                PaymentMethodType = PaymentMethodType.PayPal,
                                Details = sale.Id
                            });
                        }
                        // Catch foreign key violations because user/org could have been deleted.
                        catch(SqlException e) when(e.Number == 547) { }
                    }
                }
            }
            else if(body.Contains("\"PAYMENT.SALE.REFUNDED\""))
            {
                var ev = JsonConvert.DeserializeObject<PayPalClient.Event<PayPalClient.Refund>>(body);
                var refund = ev.Resource;
                var refundTransaction = await _transactionRepository.GetByGatewayIdAsync(
                    GatewayType.PayPal, refund.Id);
                if(refundTransaction == null)
                {
                    var saleTransaction = await _transactionRepository.GetByGatewayIdAsync(
                        GatewayType.PayPal, refund.SaleId);
                    if(saleTransaction == null)
                    {
                        return new BadRequestResult();
                    }

                    if(!saleTransaction.Refunded.GetValueOrDefault() &&
                        saleTransaction.RefundedAmount.GetValueOrDefault() < refund.TotalRefundedAmount.ValueAmount)
                    {
                        saleTransaction.RefundedAmount = refund.TotalRefundedAmount.ValueAmount;
                        if(saleTransaction.RefundedAmount == saleTransaction.Amount)
                        {
                            saleTransaction.Refunded = true;
                        }
                        await _transactionRepository.ReplaceAsync(saleTransaction);

                        var ids = refund.GetIdsFromCustom();
                        if(ids.Item1.HasValue || ids.Item2.HasValue)
                        {
                            await _transactionRepository.CreateAsync(new Core.Models.Table.Transaction
                            {
                                Amount = refund.Amount.TotalAmount,
                                CreationDate = refund.CreateTime,
                                OrganizationId = ids.Item1,
                                UserId = ids.Item2,
                                Type = TransactionType.Refund,
                                Gateway = GatewayType.PayPal,
                                GatewayId = refund.Id,
                                PaymentMethodType = PaymentMethodType.PayPal,
                                Details = refund.Id
                            });
                        }
                    }
                }
            }

            return new OkResult();
        }

        [HttpPost("ipn")]
        public async Task<IActionResult> PostIpn()
        {
            if(HttpContext?.Request?.Query == null)
            {
                return new BadRequestResult();
            }

            var key = HttpContext.Request.Query.ContainsKey("key") ?
                HttpContext.Request.Query["key"].ToString() : null;
            if(key != _billingSettings.PayPal.WebhookKey)
            {
                return new BadRequestResult();
            }

            string body = null;
            using(var reader = new StreamReader(HttpContext.Request.Body, Encoding.UTF8))
            {
                body = await reader.ReadToEndAsync();
            }

            if(string.IsNullOrWhiteSpace(body))
            {
                return new BadRequestResult();
            }

            var verified = await _paypalIpnClient.VerifyIpnAsync(body);
            if(!verified)
            {
                _logger.LogWarning("Unverified IPN received.");
                return new BadRequestResult();
            }

            var ipnTransaction = new PayPalIpnClient.IpnTransaction(body);
            if(ipnTransaction.TxnType != "web_accept" && ipnTransaction.TxnType != "merch_pmt" &&
                ipnTransaction.PaymentStatus != "Refunded")
            {
                // Only processing billing agreement payments, buy now button payments, and refunds for now.
                return new OkResult();
            }

            if(ipnTransaction.ReceiverId != _billingSettings.PayPal.BusinessId)
            {
                _logger.LogWarning("Receiver was not proper business id. " + ipnTransaction.ReceiverId);
                return new BadRequestResult();
            }

            if(ipnTransaction.PaymentType == "echeck")
            {
                // Not accepting eChecks
                _logger.LogWarning("Got an eCheck payment. " + ipnTransaction.TxnId);
                return new OkResult();
            }

            if(ipnTransaction.McCurrency != "USD")
            {
                // Only process USD payments
                _logger.LogWarning("Received a payment not in USD. " + ipnTransaction.TxnId);
                return new OkResult();
            }

            var ids = ipnTransaction.GetIdsFromCustom();
            if(!ids.Item1.HasValue && !ids.Item2.HasValue)
            {
                return new OkResult();
            }

            var isAccountCredit = ipnTransaction.IsAccountCredit();
            if(!isAccountCredit)
            {
                // Only processing credits via IPN for now
                return new OkResult();
            }

            if(ipnTransaction.PaymentStatus == "Completed")
            {
                var transaction = await _transactionRepository.GetByGatewayIdAsync(
                    GatewayType.PayPal, ipnTransaction.TxnId);
                if(transaction != null)
                {
                    _logger.LogWarning("Already processed this completed transaction. #" + ipnTransaction.TxnId);
                    return new OkResult();
                }

                try
                {
                    var tx = new Transaction
                    {
                        Amount = ipnTransaction.McGross,
                        CreationDate = ipnTransaction.PaymentDate,
                        OrganizationId = ids.Item1,
                        UserId = ids.Item2,
                        Type = isAccountCredit ? TransactionType.Credit : TransactionType.Charge,
                        Gateway = GatewayType.PayPal,
                        GatewayId = ipnTransaction.TxnId,
                        PaymentMethodType = PaymentMethodType.PayPal,
                        Details = ipnTransaction.TxnId
                    };
                    await _transactionRepository.CreateAsync(tx);

                    if(ipnTransaction.IsAccountCredit())
                    {
                        string billingEmail = null;
                        if(tx.OrganizationId.HasValue)
                        {
                            var org = await _organizationRepository.GetByIdAsync(tx.OrganizationId.Value);
                            if(org != null)
                            {
                                billingEmail = org.BillingEmailAddress();
                                if(await _paymentService.CreditAccountAsync(org, tx.Amount))
                                {
                                    await _organizationRepository.ReplaceAsync(org);
                                }
                            }
                        }
                        else
                        {
                            var user = await _userRepository.GetByIdAsync(tx.UserId.Value);
                            if(user != null)
                            {
                                billingEmail = user.BillingEmailAddress();
                                if(await _paymentService.CreditAccountAsync(user, tx.Amount))
                                {
                                    await _userRepository.ReplaceAsync(user);
                                }
                            }
                        }

                        if(!string.IsNullOrWhiteSpace(billingEmail))
                        {
                            await _mailService.SendAddedCreditAsync(billingEmail, tx.Amount);
                        }
                    }
                }
                // Catch foreign key violations because user/org could have been deleted.
                catch(SqlException e) when(e.Number == 547) { }
            }
            else if(ipnTransaction.PaymentStatus == "Refunded")
            {
                var refundTransaction = await _transactionRepository.GetByGatewayIdAsync(
                    GatewayType.PayPal, ipnTransaction.TxnId);
                if(refundTransaction != null)
                {
                    _logger.LogWarning("Already processed this refunded transaction. #" + ipnTransaction.TxnId);
                    return new OkResult();
                }

                var parentTransaction = await _transactionRepository.GetByGatewayIdAsync(
                    GatewayType.PayPal, ipnTransaction.ParentTxnId);
                if(parentTransaction == null)
                {
                    _logger.LogWarning("Parent transaction was not found. " + ipnTransaction.TxnId);
                    return new BadRequestResult();
                }

                var refundAmount = System.Math.Abs(ipnTransaction.McGross);
                var remainingAmount = parentTransaction.Amount -
                    parentTransaction.RefundedAmount.GetValueOrDefault();
                if(refundAmount > 0 && !parentTransaction.Refunded.GetValueOrDefault() &&
                    remainingAmount >= refundAmount)
                {
                    parentTransaction.RefundedAmount =
                        parentTransaction.RefundedAmount.GetValueOrDefault() + refundAmount;
                    if(parentTransaction.RefundedAmount == parentTransaction.Amount)
                    {
                        parentTransaction.Refunded = true;
                    }

                    await _transactionRepository.ReplaceAsync(parentTransaction);
                    await _transactionRepository.CreateAsync(new Transaction
                    {
                        Amount = refundAmount,
                        CreationDate = ipnTransaction.PaymentDate,
                        OrganizationId = ids.Item1,
                        UserId = ids.Item2,
                        Type = TransactionType.Refund,
                        Gateway = GatewayType.PayPal,
                        GatewayId = ipnTransaction.TxnId,
                        PaymentMethodType = PaymentMethodType.PayPal,
                        Details = ipnTransaction.TxnId
                    });
                }
            }

            return new OkResult();
        }
    }
}
