// UseWindowsForms brings System.Drawing into scope via implicit usings, which collides with
// WPF's media types. Pin every ambiguous name to its WPF meaning once, here.
global using Application = System.Windows.Application;
global using Brush = System.Windows.Media.Brush;
global using Brushes = System.Windows.Media.Brushes;
global using Color = System.Windows.Media.Color;
global using ComboBox = System.Windows.Controls.ComboBox;
global using FontFamily = System.Windows.Media.FontFamily;
global using KeyEventArgs = System.Windows.Input.KeyEventArgs;
global using ListBox = System.Windows.Controls.ListBox;
global using MouseEventArgs = System.Windows.Input.MouseEventArgs;
global using Point = System.Windows.Point;
global using RadioButton = System.Windows.Controls.RadioButton;
global using Size = System.Windows.Size;
global using TextBlock = System.Windows.Controls.TextBlock;

// Not a WinForms clash: WPF has its own System.Windows.Localization (a XAML attribute helper this
// app never uses), and it would otherwise shadow ours on every file that opens both namespaces.
global using Localization = GoPray.Services.Localization;
