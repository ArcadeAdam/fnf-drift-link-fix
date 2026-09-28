using System;
using System.ComponentModel;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Windows.Forms;

[assembly: AssemblyTitle("FNF Drift Link Fix")]
[assembly: AssemblyDescription("Fix linked race starts in Fast & Furious Drift.")]
[assembly: AssemblyProduct("FNF Drift Link Fix")]
[assembly: AssemblyVersion("1.1.0.0")]
[assembly: AssemblyFileVersion("1.1.0.0")]

namespace DriftLinkFix
{
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            using (PatchForm form = new PatchForm())
            {
                // Build checks can validate the form without opening a window or a game file.
                if (args.Length == 1 && args[0] == "--smoke-test")
                    return form.ValidateInitialState() ? 0 : 1;

                Application.Run(form);
            }
            return 0;
        }
    }

    internal sealed class PatchForm : Form
    {
        private enum Operation { Inspect, Apply, Restore }

        private sealed class WorkRequest
        {
            internal Operation Operation;
            internal string Path;
        }

        private sealed class WorkResult
        {
            internal PatchState State;
            internal bool CanRestore;
        }

        private readonly TextBox pathBox;
        private readonly Button chooseButton;
        private readonly Button applyButton;
        private readonly Button undoButton;
        private readonly Button detailsButton;
        private readonly Label statusTitle;
        private readonly TextBox statusBody;
        private readonly Label backupNote;
        private readonly BackgroundWorker worker;
        private string selectedPath;
        private string errorDetails;
        private WorkRequest currentRequest;
        private bool busy;

        internal PatchForm()
        {
            Text = "FNF Drift Link Fix";
            Font = new Font("Segoe UI", 10F);
            BackColor = Color.White;
            ForeColor = Color.FromArgb(30, 41, 59);
            AutoScaleMode = AutoScaleMode.Font;
            AutoScaleDimensions = new SizeF(7F, 17F);
            ClientSize = new Size(650, 390);
            MinimumSize = new Size(666, 429);
            StartPosition = FormStartPosition.CenterScreen;
            MaximizeBox = false;

            TableLayoutPanel layout = new TableLayoutPanel();
            layout.Dock = DockStyle.Fill;
            layout.Padding = new Padding(24, 20, 24, 16);
            layout.ColumnCount = 1;
            layout.RowCount = 7;
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            layout.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            Controls.Add(layout);

            Label heading = MakeLabel("Fix linked race starts", true);
            heading.Font = new Font(Font.FontFamily, 19F, FontStyle.Bold);
            heading.Margin = new Padding(0, 0, 0, 8);
            layout.Controls.Add(heading, 0, 0);

            Label instructions = MakeLabel("Close Drift and TeknoParrot on this cabinet first.", false);
            instructions.Margin = new Padding(0, 0, 0, 18);
            layout.Controls.Add(instructions, 0, 1);

            Label fileLabel = MakeLabel("Choose the game's sdaemon.exe file:", true);
            fileLabel.Margin = new Padding(0, 0, 0, 6);
            layout.Controls.Add(fileLabel, 0, 2);

            TableLayoutPanel fileRow = new TableLayoutPanel();
            fileRow.AutoSize = true;
            fileRow.Dock = DockStyle.Fill;
            fileRow.ColumnCount = 2;
            fileRow.RowCount = 1;
            fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            fileRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            fileRow.Margin = new Padding(0, 0, 0, 16);

            pathBox = new TextBox();
            pathBox.ReadOnly = true;
            pathBox.Dock = DockStyle.Fill;
            pathBox.Margin = new Padding(0, 6, 10, 0);
            pathBox.AccessibleName = "Selected game file";
            pathBox.BackColor = Color.White;
            fileRow.Controls.Add(pathBox, 0, 0);

            chooseButton = MakeButton("Choose game file", false);
            chooseButton.Margin = new Padding(0);
            chooseButton.Click += ChooseFile;
            fileRow.Controls.Add(chooseButton, 1, 0);
            layout.Controls.Add(fileRow, 0, 3);

            TableLayoutPanel status = new TableLayoutPanel();
            status.Dock = DockStyle.Fill;
            status.BackColor = Color.FromArgb(244, 247, 251);
            status.Padding = new Padding(14, 10, 14, 10);
            status.Margin = new Padding(0, 0, 0, 16);
            status.ColumnCount = 1;
            status.RowCount = 3;
            status.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            status.RowStyles.Add(new RowStyle(SizeType.Percent, 100F));
            status.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            status.MinimumSize = new Size(0, 124);

            statusTitle = MakeLabel("Choose your game file to begin.", true);
            statusTitle.Margin = new Padding(0, 0, 0, 7);
            statusTitle.AccessibleName = "Fix status";
            status.Controls.Add(statusTitle, 0, 0);

            statusBody = new TextBox();
            statusBody.ReadOnly = true;
            statusBody.Multiline = true;
            statusBody.BorderStyle = BorderStyle.None;
            statusBody.BackColor = status.BackColor;
            statusBody.ForeColor = ForeColor;
            statusBody.Dock = DockStyle.Fill;
            statusBody.Margin = new Padding(0);
            statusBody.ScrollBars = ScrollBars.Vertical;
            statusBody.TabStop = false;
            statusBody.Text = "The fix will check your file before making changes.";
            statusBody.AccessibleName = "Status details";
            status.Controls.Add(statusBody, 0, 1);

            backupNote = MakeLabel(String.Empty, false);
            backupNote.Font = new Font(Font.FontFamily, 9F);
            backupNote.Margin = new Padding(0, 6, 0, 0);
            backupNote.ForeColor = Color.FromArgb(71, 85, 105);
            status.Controls.Add(backupNote, 0, 2);
            layout.Controls.Add(status, 0, 4);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 0, 0, 10);

            applyButton = MakeButton("Apply fix", true);
            applyButton.Enabled = false;
            applyButton.Click += delegate { BeginWork(Operation.Apply); };
            buttons.Controls.Add(applyButton);

            undoButton = MakeButton("Undo fix", false);
            undoButton.Enabled = false;
            undoButton.Click += delegate { BeginWork(Operation.Restore); };
            buttons.Controls.Add(undoButton);

            detailsButton = MakeButton("Details", false);
            detailsButton.Visible = false;
            detailsButton.Click += delegate
            {
                MessageBox.Show(this, errorDetails ?? "No details available.",
                    "FNF Drift Link Fix", MessageBoxButtons.OK, MessageBoxIcon.Information);
            };
            buttons.Controls.Add(detailsButton);
            layout.Controls.Add(buttons, 0, 5);

            Label footer = MakeLabel("Use the fix on both cabinets.", false);
            footer.ForeColor = Color.FromArgb(71, 85, 105);
            footer.Margin = new Padding(0);
            layout.Controls.Add(footer, 0, 6);

            worker = new BackgroundWorker();
            worker.DoWork += DoWork;
            worker.RunWorkerCompleted += WorkCompleted;
            FormClosing += ClosingWhileBusy;
        }

        private Label MakeLabel(string text, bool bold)
        {
            Label label = new Label();
            label.Text = text;
            label.AutoSize = true;
            label.Font = bold ? new Font(Font, FontStyle.Bold) : Font;
            return label;
        }

        private Button MakeButton(string text, bool primary)
        {
            Button button = new Button();
            button.Text = text;
            button.AutoSize = true;
            button.MinimumSize = new Size(105, 36);
            button.Padding = new Padding(10, 3, 10, 3);
            button.Margin = new Padding(0, 0, 10, 0);
            button.FlatStyle = FlatStyle.Flat;
            button.FlatAppearance.BorderColor = primary ? Color.FromArgb(29, 78, 216) : Color.FromArgb(190, 199, 212);
            button.BackColor = primary ? Color.FromArgb(29, 78, 216) : Color.White;
            button.ForeColor = primary ? Color.White : ForeColor;
            button.UseVisualStyleBackColor = false;
            return button;
        }

        private void ChooseFile(object sender, EventArgs e)
        {
            using (OpenFileDialog dialog = new OpenFileDialog())
            {
                dialog.Title = "Choose Drift's sdaemon.exe file";
                dialog.Filter = "Drift game (sdaemon.exe)|sdaemon.exe|All files (*.*)|*.*";
                dialog.FileName = "sdaemon.exe";
                dialog.CheckFileExists = true;
                dialog.Multiselect = false;
                if (!String.IsNullOrEmpty(selectedPath))
                    dialog.InitialDirectory = Path.GetDirectoryName(selectedPath);
                else if (Directory.Exists(@"C:\FNFDRIFT\rawart"))
                    dialog.InitialDirectory = @"C:\FNFDRIFT\rawart";

                if (dialog.ShowDialog(this) != DialogResult.OK)
                    return;
                selectedPath = dialog.FileName;
                pathBox.Text = selectedPath;
                BeginWork(Operation.Inspect);
            }
        }

        private void BeginWork(Operation operation)
        {
            if (busy || String.IsNullOrEmpty(selectedPath))
                return;

            busy = true;
            chooseButton.Enabled = false;
            applyButton.Enabled = false;
            undoButton.Enabled = false;
            detailsButton.Visible = false;
            errorDetails = null;
            backupNote.Text = String.Empty;
            UseWaitCursor = true;
            statusTitle.ForeColor = ForeColor;
            statusTitle.Text = operation == Operation.Apply ? "Applying the fix..."
                : operation == Operation.Restore ? "Restoring the original game..." : "Checking your game file...";
            statusBody.Text = "Please wait. Keep the game and launcher closed.";
            currentRequest = new WorkRequest { Operation = operation, Path = selectedPath };
            worker.RunWorkerAsync(currentRequest);
        }

        private void DoWork(object sender, DoWorkEventArgs e)
        {
            WorkRequest request = (WorkRequest)e.Argument;
            PatchEngine engine = new PatchEngine();
            PatchState state = request.Operation == Operation.Apply ? engine.Apply(request.Path)
                : request.Operation == Operation.Restore ? engine.Restore(request.Path) : engine.Inspect(request.Path);
            e.Result = new WorkResult { State = state, CanRestore = state == PatchState.Patched && engine.CanRestore(request.Path) };
        }

        private void WorkCompleted(object sender, RunWorkerCompletedEventArgs e)
        {
            busy = false;
            UseWaitCursor = false;
            chooseButton.Enabled = true;
            if (e.Error != null)
            {
                statusTitle.ForeColor = Color.FromArgb(153, 27, 27);
                statusTitle.Text = currentRequest.Operation == Operation.Inspect ? "Cannot use this file"
                    : currentRequest.Operation == Operation.Restore ? "Could not restore the game" : "The fix could not be completed";
                statusBody.Text = e.Error is PatchException ? e.Error.Message
                    : "Close the game and launcher, then try again. Select Details for more help.";
                errorDetails = e.Error.Message;
                detailsButton.Visible = true;
                backupNote.Text = "Choose the game file again when you are ready to retry.";
                return;
            }

            WorkResult result = (WorkResult)e.Result;
            applyButton.Enabled = result.State == PatchState.Original;
            undoButton.Enabled = result.CanRestore;
            statusTitle.ForeColor = result.State == PatchState.Patched ? Color.FromArgb(21, 128, 61) : ForeColor;
            if (result.State == PatchState.Patched)
            {
                statusTitle.Text = currentRequest.Operation == Operation.Apply ? "Fix installed" : "Fix is already installed";
                statusBody.Text = "Done. Repeat on the other cabinet, then start the game.";
                backupNote.Text = result.CanRestore ? "Original backup saved next to the game file. Undo fix is available."
                    : "An original backup is not available here. Undo fix is unavailable.";
            }
            else
            {
                statusTitle.Text = currentRequest.Operation == Operation.Restore ? "Original game restored" : "Ready to fix";
                statusBody.Text = currentRequest.Operation == Operation.Restore ? "The fix was removed from this cabinet."
                    : "Select Apply fix. A backup of your original game file will be saved.";
                backupNote.Text = currentRequest.Operation == Operation.Restore ? "Your original backup is still saved next to the game file." : String.Empty;
            }
        }

        private void ClosingWhileBusy(object sender, FormClosingEventArgs e)
        {
            if (!busy)
                return;
            e.Cancel = true;
            statusBody.Text = "Please wait for the file check or fix to finish before closing this window.";
        }

        internal bool ValidateInitialState()
        {
            PerformLayout();
            return !busy && selectedPath == null && chooseButton.Enabled
                && !applyButton.Enabled && !undoButton.Enabled && String.IsNullOrEmpty(pathBox.Text)
                && !worker.IsBusy && statusTitle.Text == "Choose your game file to begin.";
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && worker != null)
                worker.Dispose();
            base.Dispose(disposing);
        }
    }
}
