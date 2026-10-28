// 项目同时启用 UseWPF 与 UseWindowsForms（托盘 NotifyIcon / 前台文档检测需要 WinForms），
// SDK 会注入全局 using System.Windows.Forms，导致 Button / Image / Orientation 与 WPF 同名类型冲突。
// 这里用全局别名把它们明确指向 WPF 版本（本项目从不直接构造 WinForms 的 Button / Image / Orientation）。
global using Button = System.Windows.Controls.Button;
global using Image = System.Windows.Controls.Image;
global using Orientation = System.Windows.Controls.Orientation;
// MessageBox 同理：只有 App.xaml.cs 用 WinForms 版，且是「System.Windows.Forms.MessageBox」全限定写法。
global using MessageBox = System.Windows.MessageBox;
