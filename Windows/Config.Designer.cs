namespace BARS.Windows
{
    partial class Config
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

        private void InitializeComponent()
        {
            this.pnl_airports = new vatsys.InsetPanel();
            this.lbl_icao = new vatsys.TextLabel();
            this.txt_icao = new vatsys.TextField();
            this.btn_add = new vatsys.GenericButton();
            this.txt_key = new vatsys.TextField();
            this.lbl_key = new vatsys.TextLabel();
            this.SuspendLayout();
            // 
            // pnl_airports
            // 
            this.pnl_airports.Anchor = ((System.Windows.Forms.AnchorStyles)(((System.Windows.Forms.AnchorStyles.Bottom | System.Windows.Forms.AnchorStyles.Left) 
            | System.Windows.Forms.AnchorStyles.Right)));
            this.pnl_airports.Location = new System.Drawing.Point(12, 87);
            this.pnl_airports.Name = "pnl_airports";
            this.pnl_airports.Size = new System.Drawing.Size(260, 175);
            this.pnl_airports.TabIndex = 3;
            // 
            // lbl_icao
            // 
            this.lbl_icao.AutoSize = true;
            this.lbl_icao.BackColor = System.Drawing.SystemColors.ControlDark;
            this.lbl_icao.Font = new System.Drawing.Font("Terminus (TTF)", 16F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            this.lbl_icao.ForeColor = System.Drawing.SystemColors.ControlDark;
            this.lbl_icao.HasBorder = false;
            this.lbl_icao.InteractiveText = true;
            this.lbl_icao.Location = new System.Drawing.Point(12, 15);
            this.lbl_icao.Name = "lbl_icao";
            this.lbl_icao.Size = new System.Drawing.Size(48, 17);
            this.lbl_icao.TabIndex = 0;
            this.lbl_icao.Text = "ICAO:";
            this.lbl_icao.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // txt_icao
            // 
            this.txt_icao.BackColor = System.Drawing.SystemColors.ControlDark;
            this.txt_icao.CharacterCasing = System.Windows.Forms.CharacterCasing.Upper;
            this.txt_icao.Font = new System.Drawing.Font("Terminus (TTF)", 16F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            this.txt_icao.ForeColor = System.Drawing.SystemColors.ControlDark;
            this.txt_icao.Location = new System.Drawing.Point(66, 13);
            this.txt_icao.MaxLength = 4;
            this.txt_icao.Name = "txt_icao";
            this.txt_icao.NumericCharOnly = false;
            this.txt_icao.OctalOnly = false;
            this.txt_icao.Size = new System.Drawing.Size(66, 25);
            this.txt_icao.TabIndex = 1;
            this.txt_icao.TakesReturn = false;
            this.txt_icao.KeyPress += new System.Windows.Forms.KeyPressEventHandler(this.txt_icao_KeyPress);
            // 
            // btn_add
            // 
            this.btn_add.Font = new System.Drawing.Font("Terminus (TTF)", 16F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            this.btn_add.Location = new System.Drawing.Point(138, 15);
            this.btn_add.Name = "btn_add";
            this.btn_add.Size = new System.Drawing.Size(48, 24);
            this.btn_add.SubFont = new System.Drawing.Font("Microsoft Sans Serif", 8.25F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Point, ((byte)(0)));
            this.btn_add.SubText = "";
            this.btn_add.TabIndex = 2;
            this.btn_add.Text = "ADD";
            this.btn_add.UseVisualStyleBackColor = true;
            this.btn_add.Click += new System.EventHandler(this.btn_add_Click);
            // 
            // txt_key
            // 
            this.txt_key.BackColor = System.Drawing.SystemColors.ControlDark;
            this.txt_key.Font = new System.Drawing.Font("Terminus (TTF)", 16F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            this.txt_key.ForeColor = System.Drawing.SystemColors.ControlDark;
            this.txt_key.Location = new System.Drawing.Point(90, 48);
            this.txt_key.Name = "txt_key";
            this.txt_key.NumericCharOnly = false;
            this.txt_key.OctalOnly = false;
            this.txt_key.PasswordChar = '*';
            this.txt_key.Size = new System.Drawing.Size(182, 25);
            this.txt_key.TabIndex = 5;
            this.txt_key.TakesReturn = false;
            this.txt_key.UseSystemPasswordChar = true;
            this.txt_key.TextChanged += new System.EventHandler(this.apiKeyInput_TextChanged);
            // 
            // lbl_key
            // 
            this.lbl_key.AutoSize = true;
            this.lbl_key.BackColor = System.Drawing.SystemColors.ActiveCaption;
            this.lbl_key.Font = new System.Drawing.Font("Terminus (TTF)", 16F, System.Drawing.FontStyle.Regular, System.Drawing.GraphicsUnit.Pixel);
            this.lbl_key.ForeColor = System.Drawing.SystemColors.ControlDark;
            this.lbl_key.HasBorder = false;
            this.lbl_key.InteractiveText = true;
            this.lbl_key.Location = new System.Drawing.Point(12, 48);
            this.lbl_key.Name = "lbl_key";
            this.lbl_key.Size = new System.Drawing.Size(72, 17);
            this.lbl_key.TabIndex = 4;
            this.lbl_key.Text = "API Key:";
            this.lbl_key.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            // 
            // Config
            // 
            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 17F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(284, 272);
            this.Controls.Add(this.txt_key);
            this.Controls.Add(this.lbl_key);
            this.Controls.Add(this.btn_add);
            this.Controls.Add(this.txt_icao);
            this.Controls.Add(this.lbl_icao);
            this.Controls.Add(this.pnl_airports);
            this.MaximumSize = new System.Drawing.Size(288, 300);
            this.MinimumSize = new System.Drawing.Size(288, 263);
            this.Name = "Config";
            this.Resizeable = false;
            this.Text = "BARS Config";
            this.ResumeLayout(false);
            this.PerformLayout();

        }

        private vatsys.InsetPanel pnl_airports;
        private vatsys.TextLabel lbl_icao;
        private vatsys.TextField txt_icao;
        private vatsys.GenericButton btn_add;
        private vatsys.TextField txt_key;
        private vatsys.TextLabel lbl_key;
    }
}