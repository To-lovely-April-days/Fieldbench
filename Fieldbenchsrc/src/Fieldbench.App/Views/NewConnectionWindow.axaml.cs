using Avalonia.Controls;
using Fieldbench.App.ViewModels;

namespace Fieldbench.App.Views;

public partial class NewConnectionWindow : Window
{
    public NewConnectionWindow()
    {
        InitializeComponent();
        DataContextChanged += (_, _) =>
        {
            if (DataContext is NewConnectionViewModel vm)
            {
                vm.Completed += _ => Close();
            }
        };
    }
}
