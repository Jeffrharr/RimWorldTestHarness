#!/bin/bash
set -e
cd "$(dirname "$0")/Mod"
FrameworkPathOverride=/usr/lib/mono/4.8-api /home/deck/.dotnet10/dotnet build -c Release "$@"
