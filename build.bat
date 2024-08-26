echo building....
set VERSION=0.0.0.1
set BUILD_CONFIGURATION=Release
"C:\Program Files\Microsoft Visual Studio\2022\Community\Msbuild\Current\Bin\msbuild.exe" /m /p:Configuration=%BUILD_CONFIGURATION% /p:VERSION=%VERSION%