#!/bin/sh

docker run -it --rm --network host --name packetspersecond packetspersecond $*
