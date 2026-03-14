Remove-Item -Path build\Ferry\*.pdb -Force
Compress-Archive -Path build\Ferry -DestinationPath "build\ferry_${env:VERSION}.${env:LABEL}.zip" -Force
