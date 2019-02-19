#!/usr/bin/env bash
set -e

REGISTRY="localhost:5000"
REPO="bitwarden-srp"
REG_REPO="$REGISTRY/$REPO"

DIR="$( cd "$( dirname "${BASH_SOURCE[0]}" )" && pwd )"

echo ""

if [ $# -gt 1 -a "$1" == "push" ]
then
    TAG=$2

    echo "Pushing Bitwarden ($TAG)"
    echo "========================"
    
    docker push $REG_REPO/api:$TAG
    docker push $REG_REPO/identity:$TAG
    docker push $REG_REPO/server:$TAG
    docker push $REG_REPO/attachments:$TAG
    docker push $REG_REPO/icons:$TAG
    docker push $REG_REPO/notifications:$TAG
    docker push $REG_REPO/admin:$TAG
    docker push $REG_REPO/nginx:$TAG
    docker push $REG_REPO/mssql:$TAG
    docker push $REG_REPO/setup:$TAG
elif [ $# -gt 1 -a "$1" == "tag" ]
then
    TAG=$2
    
    echo "Tagging Bitwarden as '$TAG'"
    
    docker tag $REPO/api $REG_REPO/api:$TAG
    docker tag $REPO/identity $REG_REPO/identity:$TAG
    docker tag $REPO/server $REG_REPO/server:$TAG
    docker tag $REPO/attachments $REG_REPO/attachments:$TAG
    docker tag $REPO/icons $REG_REPO/icons:$TAG
    docker tag $REPO/notifications $REG_REPO/notifications:$TAG
    docker tag $REPO/admin $REG_REPO/admin:$TAG
    docker tag $REPO/nginx $REG_REPO/nginx:$TAG
    docker tag $REPO/mssql $REG_REPO/mssql:$TAG
    docker tag $REPO/setup $REG_REPO/setup:$TAG
else
    echo "Building Bitwarden"
    echo "=================="

    chmod u+x $DIR/src/Api/build.sh
    $DIR/src/Api/build.sh

    chmod u+x $DIR/src/Identity/build.sh
    $DIR/src/Identity/build.sh

    chmod u+x $DIR/util/Server/build.sh
    $DIR/util/Server/build.sh

    chmod u+x $DIR/util/Nginx/build.sh
    $DIR/util/Nginx/build.sh

    chmod u+x $DIR/util/Attachments/build.sh
    $DIR/util/Attachments/build.sh

    chmod u+x $DIR/src/Icons/build.sh
    $DIR/src/Icons/build.sh

    chmod u+x $DIR/src/Notifications/build.sh
    $DIR/src/Notifications/build.sh

    chmod u+x $DIR/src/Admin/build.sh
    $DIR/src/Admin/build.sh

    chmod u+x $DIR/util/MsSql/build.sh
    $DIR/util/MsSql/build.sh

    chmod u+x $DIR/util/Setup/build.sh
    $DIR/util/Setup/build.sh
fi
