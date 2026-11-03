@echo off
rem Builds the KeyboardLayoutManager harness from OpenRGB's own sources and
rem writes klm_dump.tsv beside this file (#557).
rem
rem   build.cmd [OPENRGB_ROOT]
rem
rem OPENRGB_ROOT defaults to the clone beside PadForge. It must be at the commit
rem klm_harness.cpp names, which tools/gen_openrgb_keyboard_layouts.py --harness
rem writes. The build checks every vector subscript (_MSVC_STL_HARDENING=1),
rem so a read past the end of the key list stops the run instead of reading
rem memory. Needs the Visual Studio C++ tools.
setlocal
set HERE=%~dp0
set ORGB=%~1
if "%ORGB%"=="" set ORGB=%HERE%..\..\..\OpenRGB
set OUT=%TEMP%\padforge_klm_harness
if not exist "%OUT%" mkdir "%OUT%"
set VSWHERE=%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe
rem Each check jumps to its message, since a path with "(x86)" in it would
rem close an if block early.
if not exist "%VSWHERE%" goto no_vswhere
if exist "%OUT%\vs.txt" del "%OUT%\vs.txt"
if exist "%OUT%\vs.txt" goto stale_vs
set VS=
"%VSWHERE%" -latest -products * -property installationPath > "%OUT%\vs.txt"
if errorlevel 1 goto vswhere_failed
set /p VS=<"%OUT%\vs.txt"
if "%VS%"=="" goto no_vs
if not exist "%VS%\VC\Auxiliary\Build\vcvars64.bat" goto no_vcvars
rem vcvars64.bat runs vswhere.exe by its bare name, so its folder goes on PATH.
set "PATH=%PATH%;%ProgramFiles(x86)%\Microsoft Visual Studio\Installer"
call "%VS%\VC\Auxiliary\Build\vcvars64.bat" >nul || exit /b 1
set C=%ORGB%\Controllers
cl /nologo /std:c++17 /utf-8 /EHsc /MD /O1 /W0 /D_MSVC_STL_HARDENING=1 /Fe"%OUT%\klm_harness.exe" /Fo"%OUT%\\" ^
 /I"%HERE%stub" /I"%ORGB%" /I"%ORGB%\RGBController" /I"%ORGB%\KeyboardLayoutManager" /I"%ORGB%\interop" ^
 /I"%ORGB%\dependencies\json" /I"%ORGB%\dependencies\hidapi-win\include" ^
 /I"%C%\RazerController" /I"%C%\RazerController\RazerController" ^
 /I"%C%\CoolerMasterController\CMKeyboardController" /I"%C%\CorsairPeripheralV2Controller" ^
 /I"%C%\AsusAuraCoreController\AsusAuraCoreLaptopController" ^
 /I"%C%\SinowealthController\SinowealthKeyboard10cController" /I"%C%\ClevoKeyboardController" ^
 "%HERE%klm_harness.cpp" "%ORGB%\KeyboardLayoutManager\KeyboardLayoutManager.cpp" ^
 "%ORGB%\RGBController\RGBControllerKeyNames.cpp" || exit /b 1
"%OUT%\klm_harness.exe" "%HERE%klm_dump.tsv" || exit /b 1
echo wrote %HERE%klm_dump.tsv
exit /b 0

:no_vswhere
echo vswhere.exe is not at "%VSWHERE%". Install the Visual Studio C++ tools.
exit /b 1
:stale_vs
echo Could not delete the old "%OUT%\vs.txt".
exit /b 1
:vswhere_failed
echo vswhere.exe failed.
exit /b 1
:no_vs
echo vswhere.exe found no Visual Studio installation.
exit /b 1
:no_vcvars
echo "%VS%" has no C++ tools: vcvars64.bat is missing.
exit /b 1
