cls

REM Old way, no elfsharp:
REM
REM Both use the microsoft CSC under windows, so it doesn't really matter
REM Wherever csc.exe is, either in your Vis Studio folder, win folder, mono, etc
REM set path=%PATH%;c:\windows\Microsoft.NET\Framework\blahblahlbah
REM set path=%PATH%;C:\ins\cs2017comm\MSBuild\15.0\Bin\Roslyn
REM set path=%PATH%;C:\Program Files (x86)\Mono\bin\
REM cls
REM del NOTPSXSERIAL.EXE
REM del NoPS.EXE
REM pause
REM csc TransferLogic.cs NOTPSXSERIAL.cs GDB.cs Utils.cs -out:nops.exe
REM 


REM This chunk is shamelessly ripped from yoyo's excellent SO post:
REM https://stackoverflow.com/questions/328017/path-to-msbuild

REM TODO: find the command line version on windows.
set msbuild.exe=
for /D %%D in (%SYSTEMROOT%\Microsoft.NET\Framework\v4*) do set msbuild.exe=%%D\MSBuild.exe

if not defined msbuild.exe echo error: can't find MSBuild.exe & goto :eof
if not exist "%msbuild.exe%" echo error: %msbuild.exe%: not found & goto :eof

%msbuild% nops_sln.sln

pause

