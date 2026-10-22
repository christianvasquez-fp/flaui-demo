using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Navigation;
using System.Windows.Shapes;

namespace flaui_demo;

/// <summary>
/// Interaction logic for MainWindow.xaml
/// </summary>
public partial class MainWindow : Window
{
    private int _count;

    public MainWindow()
    {
        InitializeComponent();
    }

    private void MinusButton_Click(object sender, RoutedEventArgs e)
    {
        if (_count > 0)
        {
            _count--;
            CounterLabel.Content = _count.ToString();
        }
    }

    private void PlusButton_Click(object sender, RoutedEventArgs e)
    {
        _count++;
        CounterLabel.Content = _count.ToString();
    }
}
