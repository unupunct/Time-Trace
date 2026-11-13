#!/bin/bash
# usage: tools/shot.sh <Page> <name> [presetIndex]
cd "$(dirname "$0")/.."
EXE=${EXE:-src/TimeTrace.App/bin/Debug/net10.0-windows/TimeTrace.exe}
DATA="C:\Users\unupunct\AppData\Local\Temp\tt_demo_$RANDOM"
$EXE --data-dir "$DATA" --demo-data --page "$1" --preset "${3:-2}" --screenshot "D:\MyAIProjects\TimeTrace\artifacts\shots\$2.png"
