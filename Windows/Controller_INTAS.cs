using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Data;
using System.Drawing;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using vatsys;
using BARS.Util;

namespace BARS.Windows
{
    public partial class Controller_INTAS : BaseForm
    {
        // Profile property for runway configuration
        public string ActiveProfile { get; private set; }


        public Controller_INTAS(string Airport, string Profile)
        {
            InitializeComponent();
            this.Airport = Airport;
            this.ActiveProfile = Profile;
            this.Text = $"BARS - {Airport} - {Profile}";

            this.FormClosing += Controller_FormClosing;
            
            // Subscribe to stopbar events
            ControllerHandler.StopbarStateChanged += StopbarStateChanged;
        }
        
        private void Controller_FormClosing(object sender, FormClosingEventArgs e)
        {
            if (e.CloseReason == CloseReason.UserClosing)
            {
                // Unsubscribe from events to prevent memory leaks
                ControllerHandler.StopbarStateChanged -= StopbarStateChanged;
                
            }
        }
        
        /// <summary>
        /// Handles stopbar state changes from the ControllerHandler
        /// </summary>
        private void StopbarStateChanged(object sender, StopbarEventArgs e)
        {
            // Only process events for this airport and window type
            if (e.Stopbar.Airport == this.Airport && e.WindowType == WindowType.INTAS)
            {
                // Update the UI based on the stopbar state
                UpdateStopbarUI(e.Stopbar);
            }
        }
        
        /// <summary>
        /// Updates the UI elements for a stopbar
        /// </summary>
        private void UpdateStopbarUI(Stopbar stopbar)
        {
            // Need to invoke on the UI thread if called from a background thread
            if (this.InvokeRequired)
            {
                this.Invoke(new Action<Stopbar>(UpdateStopbarUI), stopbar);
                return;
            }
            
            // TODO: Update the specific UI elements for this stopbar
            // This will be implemented in the future when you have the visual elements ready
            // Example:
            // if (controlDictionary.TryGetValue(stopbar.BARSId, out Control control))
            // {
            //     control.BackColor = stopbar.State ? Color.Red : Color.Green;
            // }
        }
        
        /// <summary>
        /// Toggles a stopbar's state
        /// </summary>
        public void ToggleStopbar(string barsId, bool autoRaise = true)
        {
            ControllerHandler.ToggleStopbar(this.Airport, barsId, WindowType.INTAS, autoRaise);
        }
        
        /// <summary>
        /// Sets a stopbar's state
        /// </summary>
        public void SetStopbarState(string barsId, bool state, bool autoRaise = true)
        {
            ControllerHandler.SetStopbarState(this.Airport, barsId, state, WindowType.INTAS, autoRaise);
        }

        public string Airport { get; set; }
    }
}
