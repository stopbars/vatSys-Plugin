namespace BARS.Windows
{
    partial class Profiles
    {
        /// <summary>
        /// Required designer variable.
        /// </summary>
        private System.ComponentModel.IContainer components = null;

        #region Windows Form Designer generated code

        /// <summary>
        /// Required method for Designer support - do not modify
        /// the contents of this method with the code editor.
        /// </summary>
        private void InitializeComponent()
        {
            this.pnl_profiles = new vatsys.InsetPanel();
            this.lbl_header = new vatsys.TextLabel();
            this.SuspendLayout();
            // 
            // pnl_profiles
            // 
            this.pnl_profiles.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom) 
            | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.pnl_profiles.Location = new System.Drawing.Point(12, 40);
            this.pnl_profiles.Name = "pnl_profiles";
            this.pnl_profiles.Size = new System.Drawing.Size(353, 398);
            this.pnl_profiles.TabIndex = 0;
            // 
            // lbl_header
            // 
            this.lbl_header.AutoSize = true;
            this.lbl_header.Font = new System.Drawing.Font("Terminus (TTF)", 16F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            this.lbl_header.ForeColor = System.Drawing.SystemColors.ControlDark;
            this.lbl_header.HasBorder = false;
            this.lbl_header.InteractiveText = true;
            this.lbl_header.Location = new System.Drawing.Point(12, 12);
            this.lbl_header.Name = "lbl_header";
            this.lbl_header.Size = new System.Drawing.Size(183, 17);
            this.lbl_header.TabIndex = 1;
            this.lbl_header.Text = "Select Runway Profile:";
            this.lbl_header.TextAlign = System.Drawing.ContentAlignment.MiddleLeft;
            // 
            // Profiles
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 17F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(377, 450);
            this.Controls.Add(this.lbl_header);
            this.Controls.Add(this.pnl_profiles);
            this.MinimumSize = new System.Drawing.Size(300, 200);
            this.Name = "Profiles";
            this.Text = "Runway Profiles";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        #endregion

        private vatsys.InsetPanel pnl_profiles;
        private vatsys.TextLabel lbl_header;
    }
}