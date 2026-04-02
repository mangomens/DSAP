# DSAP Quick Redeploy Commands

1. Redeploy DSAP EXE and copy into `C:\Users\ngold\Workspace\ArchipelagoDev\DSAP`

```bash
cd /mnt/c/Users/ngold/Workspace/ArchipelagoDev/DSAP && dotnet publish source/DSAP.Desktop/DSAP.Desktop.csproj -c Release -r win-x64 && cp -f source/DSAP.Desktop/bin/Release/net8.0/win-x64/publish/DSAP.Desktop.exe /mnt/c/Users/ngold/Workspace/ArchipelagoDev/DSAP/DSAP.Desktop.exe
```

2. Redeploy `dsr.apworld` and copy into `C:\ProgramData\Archipelago\custom_worlds`

```bash
cd /mnt/c/Users/ngold/Workspace/ArchipelagoDev/DSAP/apworld && python3 -c "import shutil; shutil.make_archive('dsr', 'zip', '.', 'dsr'); shutil.move('dsr.zip', '/mnt/c/ProgramData/Archipelago/custom_worlds/dsr.apworld')" && echo "apworld deployed"
```
