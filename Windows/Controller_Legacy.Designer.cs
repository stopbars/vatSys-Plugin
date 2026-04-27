namespace BARS.Windows
{
    partial class Controller_Legacy
    {
        private System.ComponentModel.IContainer components = null;

        protected override void Dispose(bool disposing)
        {
            if (disposing && (components != null))
            {
                components.Dispose();
            }

            base.Dispose(disposing);
        }

        #region Windows Form Designer generated code

        private void InitializeComponent()
        {
            this.pnl_legacy = new System.Windows.Forms.Panel();
            this.pnl_1_runway = new System.Windows.Forms.Panel();
            this.lbl_R = new System.Windows.Forms.Label();
            this.lbl_L = new System.Windows.Forms.Label();
            this.pnl_2_runway = new System.Windows.Forms.Panel();
            this.lbl_B = new System.Windows.Forms.Label();
            this.lbl_T = new System.Windows.Forms.Label();
            this.pnl_legacy.SuspendLayout();
            this.pnl_1_runway.SuspendLayout();
            this.pnl_2_runway.SuspendLayout();
            this.SuspendLayout();
            // 
            // pnl_legacy
            // 
            this.pnl_legacy.BackColor = System.Drawing.Color.Black;
            this.pnl_legacy.Controls.Add(this.pnl_1_runway);
            this.pnl_legacy.Controls.Add(this.pnl_2_runway);
            this.pnl_legacy.Dock = System.Windows.Forms.DockStyle.Fill;
            this.pnl_legacy.Location = new System.Drawing.Point(0, 0);
            this.pnl_legacy.Name = "pnl_legacy";
            this.pnl_legacy.Size = new System.Drawing.Size(1067, 535);
            this.pnl_legacy.TabIndex = 0;
            // 
            // pnl_1_runway
            // 
            this.pnl_1_runway.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Left | System.Windows.Forms.AnchorStyles.Right)));
            this.pnl_1_runway.BackColor = System.Drawing.Color.Gray;
            this.pnl_1_runway.Controls.Add(this.lbl_R);
            this.pnl_1_runway.Controls.Add(this.lbl_L);
            this.pnl_1_runway.Location = new System.Drawing.Point(0, 237);
            this.pnl_1_runway.Name = "pnl_1_runway";
            this.pnl_1_runway.Size = new System.Drawing.Size(1067, 60);
            this.pnl_1_runway.TabIndex = 0;
            this.pnl_1_runway.Visible = false;
            // 
            // lbl_R
            // 
            this.lbl_R.BackColor = System.Drawing.Color.Transparent;
            this.lbl_R.Dock = System.Windows.Forms.DockStyle.Right;
            this.lbl_R.Font = new System.Drawing.Font("Arial", 18F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            this.lbl_R.ForeColor = System.Drawing.Color.White;
            this.lbl_R.Location = new System.Drawing.Point(979, 0);
            this.lbl_R.Name = "lbl_R";
            this.lbl_R.Size = new System.Drawing.Size(64, 60);
            this.lbl_R.TabIndex = 7;
            this.lbl_R.Text = "16R";
            this.lbl_R.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // lbl_L
            // 
            this.lbl_L.BackColor = System.Drawing.Color.Transparent;
            this.lbl_L.Dock = System.Windows.Forms.DockStyle.Left;
            this.lbl_L.Font = new System.Drawing.Font("Arial", 18F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            this.lbl_L.ForeColor = System.Drawing.Color.White;
            this.lbl_L.Location = new System.Drawing.Point(0, 0);
            this.lbl_L.Name = "lbl_L";
            this.lbl_L.Size = new System.Drawing.Size(64, 60);
            this.lbl_L.TabIndex = 6;
            this.lbl_L.Text = "34L";
            this.lbl_L.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // pnl_2_runway
            // 
            this.pnl_2_runway.Anchor = ((System.Windows.Forms.AnchorStyles)((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)));
            this.pnl_2_runway.BackColor = System.Drawing.Color.Gray;
            this.pnl_2_runway.Controls.Add(this.lbl_B);
            this.pnl_2_runway.Controls.Add(this.lbl_T);
            this.pnl_2_runway.Location = new System.Drawing.Point(504, 0);
            this.pnl_2_runway.Name = "pnl_2_runway";
            this.pnl_2_runway.Size = new System.Drawing.Size(59, 535);
            this.pnl_2_runway.TabIndex = 1;
            this.pnl_2_runway.Visible = false;
            // 
            // lbl_B
            // 
            this.lbl_B.BackColor = System.Drawing.Color.Transparent;
            this.lbl_B.Dock = System.Windows.Forms.DockStyle.Bottom;
            this.lbl_B.Font = new System.Drawing.Font("Arial", 18F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            this.lbl_B.ForeColor = System.Drawing.Color.White;
            this.lbl_B.Location = new System.Drawing.Point(0, 487);
            this.lbl_B.Name = "lbl_B";
            this.lbl_B.Size = new System.Drawing.Size(59, 48);
            this.lbl_B.TabIndex = 9;
            this.lbl_B.Text = "25";
            this.lbl_B.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // lbl_T
            // 
            this.lbl_T.BackColor = System.Drawing.Color.Transparent;
            this.lbl_T.Dock = System.Windows.Forms.DockStyle.Top;
            this.lbl_T.Font = new System.Drawing.Font("Arial", 18F, System.Drawing.FontStyle.Bold, System.Drawing.GraphicsUnit.Pixel);
            this.lbl_T.ForeColor = System.Drawing.Color.White;
            this.lbl_T.Location = new System.Drawing.Point(0, 0);
            this.lbl_T.Name = "lbl_T";
            this.lbl_T.Size = new System.Drawing.Size(59, 48);
            this.lbl_T.TabIndex = 8;
            this.lbl_T.Text = "07";
            this.lbl_T.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // Controller_Legacy
            // 
            GenerateLegacyLayout();
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 17F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(1067, 535);
            this.Controls.Add(this.pnl_legacy);
            this.MinimumSize = new System.Drawing.Size(565, 350);
            this.Name = "Controller_Legacy";
            this.Text = "Controller";
            this.pnl_legacy.ResumeLayout(false);
            this.pnl_1_runway.ResumeLayout(false);
            this.pnl_2_runway.ResumeLayout(false);
            this.ResumeLayout(false);
        }

        #endregion

        private System.Windows.Forms.Panel pnl_legacy;
        private System.Windows.Forms.Panel pnl_1_runway;
        private System.Windows.Forms.Panel pnl_2_runway;
        private System.Windows.Forms.Label lbl_L;
        private System.Windows.Forms.Label lbl_R;
        private System.Windows.Forms.Label lbl_B;
        private System.Windows.Forms.Label lbl_T;
    }
}
