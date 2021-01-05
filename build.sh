#!/bin/bash

clear

rm NOTPSXSERIAL.EXE
rm NoPS.EXE
rm nops.exe

csc TransferLogic.cs NOTPSXSERIAL.cs GDB.cs Utils.cs -out:nops.exe



