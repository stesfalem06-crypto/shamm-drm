using System.Windows;
using System.Windows.Controls;
using XSeller.Services;

namespace XSeller.Views;

/// <summary>Startup / switch-user login (only when accounts are enabled).</summary>
public static class LoginWindow
{
    /// <summary>Returns the signed-in user, or null when cancelled.</summary>
    public static UserAccount? Show(Window? owner, string shopName, bool allowCancel)
    {
        var users = Accounts.Instance.ListUsers().Where(u => !u.Disabled).OrderBy(u => u.Role).ThenBy(u => u.Name).ToList();
        var win = Ui.Dialog("X Seller — sign in", 420, 330, owner);
        win.ResizeMode = ResizeMode.NoResize;
        var userBox = new ComboBox { ItemsSource = users, DisplayMemberPath = "Name", SelectedIndex = users.Count > 0 ? 0 : -1, Padding = new Thickness(6, 4, 6, 4) };
        var lastId = Accounts.Instance.Get("last_user");
        var last = users.FindIndex(u => u.Id == lastId);
        if (last >= 0) userBox.SelectedIndex = last;
        var pw = new PasswordBox { Padding = new Thickness(6, 4, 6, 4) };
        var err = Ui.Text("", fg: Ui.Warn);
        UserAccount? result = null;
        var signIn = Ui.Button("Sign in", (_, _) =>
        {
            if (userBox.SelectedItem is not UserAccount u) { err.Text = "Pick your name."; return; }
            var ok = Accounts.Instance.Verify(u.Id, pw.Password);
            if (ok == null) { err.Text = "Wrong PIN / password."; pw.Clear(); pw.Focus(); return; }
            Accounts.Instance.Set("last_user", ok.Id);
            result = ok;
            win.DialogResult = true;
        }, isDefault: true);
        var cancel = Ui.Button(allowCancel ? "Cancel" : "Exit", (_, _) => win.DialogResult = false);
        cancel.IsCancel = true;
        win.Content = Ui.Scroll(Ui.Stack(
            Ui.Text("X Seller", 20, bold: true),
            Ui.Text(shopName, fg: Ui.Muted),
            Ui.Labeled("User", userBox, 110),
            Ui.Labeled("PIN / password", pw, 110),
            err,
            Ui.Row(signIn, cancel)));
        win.Loaded += (_, _) => pw.Focus();
        win.ShowDialog();
        return result;
    }

    /// <summary>"Enable accounts": create the first admin (owner) account.</summary>
    public static UserAccount? EnableAccounts(Window owner)
    {
        var win = Ui.Dialog("Enable accounts", 480, 380, owner);
        var name = new TextBox { Padding = new Thickness(4) };
        var p1 = new PasswordBox { Padding = new Thickness(4) };
        var p2 = new PasswordBox { Padding = new Thickness(4) };
        var err = Ui.Text("", fg: Ui.Warn);
        UserAccount? result = null;
        win.Content = Ui.Scroll(Ui.Stack(
            Ui.Text("Create the owner (admin) account", 16, bold: true),
            Ui.Text("After this, X Seller asks for a user + PIN at startup and every sale is attributed to the signed-in employee. " +
                    "Sales before this point stay under “Default”.", fg: Ui.Muted),
            Ui.Labeled("Owner name", name),
            Ui.Labeled("PIN / password", p1),
            Ui.Labeled("Repeat", p2),
            err,
            Ui.Row(Ui.Button("Enable", (_, _) =>
            {
                try
                {
                    if (string.IsNullOrWhiteSpace(name.Text)) throw new ArgumentException("Enter a name.");
                    if (p1.Password != p2.Password) throw new ArgumentException("PINs don't match.");
                    result = Accounts.Instance.EnableWithFirstAdmin(name.Text.Trim(), p1.Password);
                    win.DialogResult = true;
                }
                catch (Exception ex) { err.Text = ex.Message; }
            }, true), Ui.Button("Cancel", (_, _) => win.DialogResult = false))));
        win.ShowDialog();
        return result;
    }
}
