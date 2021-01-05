cls

REM Both use the microsoft CSC under windows, so it doesn't really matter
REM Wherever csc.exe is, either in your Vis Studio folder, win folder, mono, etc

REM set path=%PATH%;c:\windows\Microsoft.NET\Framework\blahblahlbah
REM set path=%PATH%;C:\ins\cs2017comm\MSBuild\15.0\Bin\Roslyn
REM set path=%PATH%;C:\Program Files (x86)\Mono\bin\

set path=%PATH%;C:\Program Files (x86)\Mono\bin\

cls

del NOTPSXSERIAL.EXE
del NoPS.EXE
pause
csc TransferLogic.cs NOTPSXSERIAL.cs GDB.cs Utils.cs -out:nops.exe

pause

