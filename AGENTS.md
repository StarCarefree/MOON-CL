# 协作约定

- 测试、基准、构建等临时产物统一放 `artifacts/test/<主题>/`（该目录已 gitignore）。不要往 D:/data 根目录或 %TEMP% 乱放；现有脚本可通过环境变量 `PI_SCRATCH_DIR` 指向该目录复用。
- gitignore 内的产物路径用 shell/python 操作。
- 每次改动结束后都要重新构建单 exe 供用户检查：`pwsh -NoProfile -File scripts/Publish-SingleExe.ps1 -Architecture x64`（产物 `artifacts/single-exe-win-x64/PCL-MOON.exe`），并告知产物路径与时间。正在运行的旧启动器需用户关闭后重开才能看到改动。

## startup-agent 构建与测试

```bash
python -X utf8 tools/startup-agent/build.py --javac 'C:/Program Files/Java/jdk-17/bin/javac.exe'
python -X utf8 tools/startup-agent/test.py --java 'C:/Program Files/Java/jdk-17/bin/java.exe'
python -X utf8 tools/startup-agent/test.py --java 'C:/Program Files/Zulu/zulu-21/bin/java.exe'
dotnet test PCL.Core.Test/PCL.Core.Test.csproj -c Release -p:UseSharedCompilation=false
```

详见 tools/startup-agent/README.md。
