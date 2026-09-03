using System.Windows;

namespace XamaMaster;

public partial class PasswordPromptWindow : Window
{
    public string? EnteredPassword { get; private set; }

    public PasswordPromptWindow(string prompt, string? error = null)
    {
        InitializeComponent();
        PromptLabel.Text = prompt;
        if (error != null) ErrorText.Text = error;
    }

    private void Ok_Click(object sender, RoutedEventArgs e)
    {
        EnteredPassword = PasswordInput.Password;
        DialogResult = true;
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }
}
