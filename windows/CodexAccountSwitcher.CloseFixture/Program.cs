using System.Drawing;
using System.Windows.Forms;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        Application.EnableVisualStyles();
        Application.SetCompatibleTextRenderingDefault(false);
        var form = new Form
        {
            Text = "Codex package update test fixture",
            ClientSize = new Size(360, 90),
            FormBorderStyle = FormBorderStyle.FixedToolWindow,
            StartPosition = FormStartPosition.CenterScreen,
            ShowInTaskbar = true
        };

        // Isolated replica of the Store app's X = hide behavior.
        // Only the File -> Quit ChatGPT menu exits when enabled.
        var mode = Environment.GetEnvironmentVariable("CODEX_CLOSE_FIXTURE_MODE");
        if (mode is "menu" or "no-menu")
        {
            var quitInvoked = false;
            form.FormClosing += (_, e) =>
            {
                if (!quitInvoked)
                {
                    e.Cancel = true;
                    form.Hide();
                }
            };
            if (mode == "menu")
            {
                var menu = new MenuStrip();
                var file = new ToolStripMenuItem("File");
                var quit = new ToolStripMenuItem("Quit ChatGPT");
                quit.Click += (_, _) => { quitInvoked = true; form.Close(); };
                file.DropDownItems.Add(quit);
                menu.Items.Add(file);
                form.MainMenuStrip = menu;
                form.Controls.Add(menu);
            }
        }
        Application.Run(form);
    }
}
