#!/bin/bash

clear

rm NOTPSXSERIAL.EXE
rm NoPS.EXE
rm nops.exe

# Old way, no elfsharp
# csc TransferLogic.cs NOTPSXSERIAL.cs GDB.cs Utils.cs -out:nops.exe

msbuild nops_sln.sln


