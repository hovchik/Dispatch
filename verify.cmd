@echo off
rem Builds and tests the solution, writing output to .build\*.log (used for CI-less local verification).
cd /d "%~dp0"
if not exist .build mkdir .build
echo RUNNING> .build\status.txt
dotnet --list-sdks > .build\sdks.log 2>&1
dotnet build Dispatch.slnx -c Debug -nologo "-clp:NoSummary;ErrorsOnly" > .build\build.log 2>&1
echo BUILD_EXIT=%ERRORLEVEL%>> .build\status.txt
dotnet test Dispatch.slnx --no-build -nologo > .build\test.log 2>&1
echo TEST_EXIT=%ERRORLEVEL%>> .build\status.txt
echo DONE>> .build\status.txt
