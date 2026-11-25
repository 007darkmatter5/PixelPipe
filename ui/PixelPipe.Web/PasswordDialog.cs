namespace PixelPipe.Web;

/// <summary>
/// Password reset window used by the tray and by PixelPipe.Web.exe --reset-password.
/// The Windows publish compiles the dialog. Other builds report that no window is available
/// so the caller can read a console instead.
/// </summary>
static class PasswordDialog
{
    public static bool? TryCollect(string? preset, out string username, out string password)
    {
#if WINDOWS_UI
        var holder = new Collected();
        RunSta(() => holder.Result = Show(preset, holder));
        username = holder.Username;
        password = holder.Password;
        return holder.Result;
#else
        username = "";
        password = "";
        return null;
#endif
    }

    public static void Tell(string message, bool success)
    {
#if WINDOWS_UI
        RunSta(() =>
        {
            System.Windows.Forms.MessageBox.Show(
                message,
                "PixelPipe",
                System.Windows.Forms.MessageBoxButtons.OK,
                success
                    ? System.Windows.Forms.MessageBoxIcon.Information
                    : System.Windows.Forms.MessageBoxIcon.Error);
        });
#else
        if (success)
        {
            Console.WriteLine(message);
        }
        else
        {
            Console.Error.WriteLine(message);
        }
#endif
    }

#if WINDOWS_UI
    private static void RunSta(Action action)
    {
        Exception? error = null;
        var thread = new Thread(() =>
        {
            try
            {
                action();
            }
            catch (Exception ex)
            {
                error = ex;
            }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();
        if (error is not null)
        {
            throw error;
        }
    }

    private static bool Show(string? preset, Collected holder)
    {
        System.Windows.Forms.Application.EnableVisualStyles();
        System.Windows.Forms.Application.SetCompatibleTextRenderingDefault(false);
        using var form = new System.Windows.Forms.Form
        {
            Text = "Reset PixelPipe password",
            FormBorderStyle = System.Windows.Forms.FormBorderStyle.FixedDialog,
            MaximizeBox = false,
            MinimizeBox = false,
            StartPosition = System.Windows.Forms.FormStartPosition.CenterScreen,
            TopMost = true,
            Width = 440,
            Height = 300,
        };
        var user = new System.Windows.Forms.TextBox { Left = 16, Top = 28, Width = 390, Text = preset ?? "" };
        var password = new System.Windows.Forms.TextBox
        {
            Left = 16,
            Top = 76,
            Width = 390,
            UseSystemPasswordChar = true,
        };
        var confirm = new System.Windows.Forms.TextBox
        {
            Left = 16,
            Top = 124,
            Width = 390,
            UseSystemPasswordChar = true,
        };
        form.Controls.Add(new System.Windows.Forms.Label { Left = 16, Top = 8, Width = 390, Text = "Username" });
        form.Controls.Add(user);
        form.Controls.Add(new System.Windows.Forms.Label { Left = 16, Top = 56, Width = 390, Text = "New password" });
        form.Controls.Add(password);
        form.Controls.Add(new System.Windows.Forms.Label { Left = 16, Top = 104, Width = 390, Text = "Confirm password" });
        form.Controls.Add(confirm);
        var show = new System.Windows.Forms.CheckBox { Left = 16, Top = 156, Width = 200, Text = "Show password" };
        show.CheckedChanged += (_, _) =>
        {
            password.UseSystemPasswordChar = !show.Checked;
            confirm.UseSystemPasswordChar = !show.Checked;
        };
        form.Controls.Add(show);
        var ok = new System.Windows.Forms.Button { Text = "Reset", Left = 210, Top = 200, Width = 90 };
        var cancel = new System.Windows.Forms.Button
        {
            Text = "Cancel",
            Left = 310,
            Top = 200,
            Width = 90,
            DialogResult = System.Windows.Forms.DialogResult.Cancel,
        };
        form.AcceptButton = ok;
        form.CancelButton = cancel;
        form.Controls.Add(ok);
        form.Controls.Add(cancel);
        ok.Click += (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(user.Text))
            {
                System.Windows.Forms.MessageBox.Show("Enter the username.", "PixelPipe");
                return;
            }

            if (password.Text.Length < 8 || password.Text != confirm.Text)
            {
                System.Windows.Forms.MessageBox.Show(
                    password.Text.Length < 8
                        ? "Password must be at least 8 characters."
                        : "The passwords do not match.",
                    "PixelPipe");
                return;
            }

            holder.Username = user.Text.Trim();
            holder.Password = password.Text;
            form.DialogResult = System.Windows.Forms.DialogResult.OK;
            form.Close();
        };
        if (!string.IsNullOrWhiteSpace(preset))
        {
            form.Shown += (_, _) => password.Focus();
        }

        return form.ShowDialog() == System.Windows.Forms.DialogResult.OK;
    }

    private sealed class Collected
    {
        public bool Result { get; set; }
        public string Username { get; set; } = "";
        public string Password { get; set; } = "";
    }
#endif
}
