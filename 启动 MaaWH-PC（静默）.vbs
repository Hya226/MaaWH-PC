' MaaWH-PC 静默启动器：隐藏窗口执行启动脚本（同步清单 + 设便携 .NET + 启动 GUI）
Set sh = CreateObject("Wscript.Shell")
sh.CurrentDirectory = "E:\MaaWH-PC"
sh.Run "E:\MaaWH-PC\start_maawh.bat", 0, False
