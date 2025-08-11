using System.Drawing;
using vatsys;

namespace BARS.Windows
{
    partial class Controller_INTAS
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
            this.airportMapControl = new AirportMapControl();
            this.SuspendLayout();



            this.airportMapControl.Anchor = ((System.Windows.Forms.AnchorStyles)((((System.Windows.Forms.AnchorStyles.Top | System.Windows.Forms.AnchorStyles.Bottom)
            | System.Windows.Forms.AnchorStyles.Left)
            | System.Windows.Forms.AnchorStyles.Right)));
            this.airportMapControl.BackColor = Color.FromArgb(93, 93, 93);
            this.airportMapControl.Location = new System.Drawing.Point(12, 12);
            this.airportMapControl.Name = "airportMapControl";
            this.airportMapControl.Size = new System.Drawing.Size(500, 500);
            this.airportMapControl.TabIndex = 0;



            this.AutoScaleDimensions = new System.Drawing.SizeF(8F, 17F);
            this.AutoScaleMode = System.Windows.Forms.AutoScaleMode.Font;
            this.ClientSize = new System.Drawing.Size(500, 560);
            this.MinimumSize = new System.Drawing.Size(250, 250);
            this.Controls.Add(this.airportMapControl);
            this.HasMaximizeButton = true;
            this.MiddleClickClose = false;
            this.Name = "Controller_INTAS";
            this.Text = "Controller_INTAS";
            this.ResumeLayout(false);
        }
        #endregion

        private AirportMapControl airportMapControl;
    }
}