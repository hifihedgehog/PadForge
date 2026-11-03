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
"%ProgramFiles(x86)%\Microsoft Visual Studio\Installer\vswhere.exe" -latest -products * -property installationPath > "%OUT%\vs.txt" || exit /b 1
set /p VS=<"%OUT%\vs.txt"
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
