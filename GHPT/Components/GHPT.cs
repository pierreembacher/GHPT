using GH_IO.Serialization;
using GHPT.Configs;
using GHPT.IO;
using GHPT.Prompts;
using GHPT.UI;
using GHPT.Utils;
using Grasshopper.Kernel;
using Grasshopper.Kernel.Special;
using System.Collections;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GHPT.Components
{
    public class GHPT : GH_Component, IGH_InitCodeAware
    {
        private GH_Document _doc;
        private PromptData _data;
        private readonly Spinner _spinner;

        private string previousPrompt = string.Empty;
               
        private bool allowDupPrompt = false;

        public bool PromptOverride
        {
            get { return allowDupPrompt; }
            set { allowDupPrompt = value; }
        }


        private readonly Queue _queue;
        /// <summary>
        /// Each implementation of GH_Component must provide a public 
        /// constructor without any arguments.
        /// Category represents the Tab in which the component will appear, 
        /// Subcategory the panel. If you use non-existing tab or panel names, 
        /// new tabs/panels will automatically be created.
        /// </summary>
        public GHPT()
          : base("GHPT", "GHPT",
            "A component that lets you use ChatGPT to instantiate Grasshopper snippets from a prompt",
            "Extra", "GHPT")
        {
            Ready += OnReady;
            _queue = new Queue();
            this.Message = ConfigUtil.CurrentConfig.Model;
            _spinner = new Spinner(this);
            RegenerateComponentUI();
        }

        public override void CreateAttributes()
        {
            m_attributes = new CustomAttributes(this);
        }

        public override void AppendAdditionalMenuItems(ToolStripDropDown menu)
        {
            base.AppendAdditionalMenuItems(menu);

            Menu_AppendSeparator(menu);

            var configs = ConfigUtil.ConfigList;
            foreach (GPTConfig config in configs)
            {
                Menu_AppendItem(menu, config.Name, (sender, args) =>
                {
                    ConfigUtil.CurrentConfig = config;
                    RegenerateComponentUI();

                }, true, ConfigUtil.CurrentConfig.Name == config.Name);
            }

            Menu_AppendItem(menu, "Add Config", (sender, args) =>
            {
                var modal = new ConfigPromptModal();
                var result = modal.ShowModal();

                if (result == Eto.Forms.DialogResult.Cancel)
                    return;

                if (!modal.config.IsValid())
                    return;

                ConfigUtil.SaveConfig(modal.config);
                ConfigUtil.CurrentConfig = ConfigUtil.ConfigList.Last();
                RegenerateComponentUI();
            });

            Menu_AppendItem(menu, "Remove Current Config", (sender, args) =>
            {
                ConfigUtil.RemoveConfig(ConfigUtil.CurrentConfig);
                ConfigUtil.CurrentConfig = ConfigUtil.ConfigList.First();
                RegenerateComponentUI();
            }, ConfigUtil.ConfigList.Count > 0);

            Menu_AppendSeparator(menu);

            ToolStripMenuItem item = Menu_AppendItem(menu, "Duplicate Prompt Once", Menu_PromptOverride, true, false);
            item.ToolTipText = "Duplicate prompts are usually disallowed by this component.";
            }
        private void Menu_PromptOverride(object sender, EventArgs e)
        {
            //RecordUndoEvent("PromptOverride");
            PromptOverride = !PromptOverride;
            ExpireSolution(true);
        }
        /*
        public override bool Write(GH_IWriter writer)
        {
            writer.SetBoolean("PromptOverride", PromptOverride);
            return base.Write(writer);
        }
        public override bool Read(GH_IReader reader)
        {
            PromptOverride = reader.GetBoolean("PromptOverride");
            return base.Read(reader);
        }
         */

        private void OnReady(object sender, EventArgs e)
        {
            _spinner.Stop();
            this.AddComponents();
            this.ConnectComponents();
            RegenerateComponentUI();

            _doc.NewSolution(true, GH_SolutionMode.Silent);
        }

        private void RegenerateComponentUI()
        {
            DestroyIconCache();
            SetIconOverride(Icon);
            SetGPTMessage();
            Grasshopper.Instances.RedrawCanvas();
        }

        private void SetGPTMessage()
        {
            this.Message = ConfigUtil.CurrentConfig.Model?.ToString()?.Replace('_', '.');
        }

        /// <summary>
        /// Registers all the input parameters for this component.
        /// </summary>
        protected override void RegisterInputParams(GH_InputParamManager pManager)
        {
            pManager.AddTextParameter("Prompt", "P", "LLM prompt for instantiating components", GH_ParamAccess.item);
            pManager.AddNumberParameter("Temperature", "T", "Controls how \"creatively\" the network responds to your prompt", GH_ParamAccess.item, 0.7);

            pManager[1].Optional = true;
        }

        /// <summary>
        /// Registers all the output parameters for this component.
        /// </summary>
        protected override void RegisterOutputParams(GH_OutputParamManager pManager)
        {
        }

        /// <summary>
        /// This is the method that actually does the work.
        /// </summary>
        /// <param name="DA">The DA object can be used to retrieve data from input parameters and 
        /// to store data in output parameters.</param>
        protected async override void SolveInstance(IGH_DataAccess DA)
        {
            _doc = OnPingDocument();

            bool configured = ConfigUtil.CheckConfiguration();

            if (!configured)
            {
                var modal = new ConfigPromptModal();
                var result = modal.ShowModal();

                if (result == Eto.Forms.DialogResult.Cancel)
                    return;

                if (!modal.config.IsValid())
                    return;

                ConfigUtil.SaveConfig(modal.config);
            }

            ConfigUtil.LoadConfigs();

            string prompt = string.Empty;
            double temperature = 0.7;

            DA.GetData(0, ref prompt);
            DA.GetData(1, ref temperature);

            if (string.IsNullOrEmpty(prompt))
            {
                previousPrompt = prompt;
                return;
            }

            if (prompt == previousPrompt && !PromptOverride)
                return;
            previousPrompt = prompt;
            PromptOverride = false;

            Task.Run(() =>
            {
                _spinner.Start();
            });
            _data = await PromptUtils.AskQuestion(prompt);
            Ready?.Invoke(this, new EventArgs());
        }

        public event EventHandler Ready;

        public void AddComponents()
        {

            if (!string.IsNullOrEmpty(_data.Advice))
                this.CreateAdvicePanel(_data.Advice);

            if (_data.Additions is null)
                return;

            // Compute tiers
            Dictionary<int, List<Addition>> buckets = new();

            foreach (Addition addition in _data.Additions)
            {
                if (buckets.ContainsKey(addition.Tier))
                {
                    buckets[addition.Tier].Add(addition);
                }
                else
                {
                    buckets.Add(addition.Tier, new List<Addition>() { addition });
                }
            }

            foreach (int tier in buckets.Keys)
            {
                int xIncrement = 250;
                int yIncrement = 100;
                float x = this.Attributes.Pivot.X + 100 + (xIncrement * tier);
                float y = this.Attributes.Pivot.Y;

                foreach (Addition addition in buckets[tier])
                {
                    GraphUtil.InstantiateComponent(_doc, addition, new System.Drawing.PointF(x, y));
                    y += yIncrement;
                }
            }
        }

        private void ConnectComponents()
        {
            if (_data.Connections is null)
                return;

            foreach (ConnectionPairing connection in _data.Connections)
            {
                GraphUtil.ConnectComponent(_doc, connection);
            }
        }

        protected override void AfterSolveInstance()
        {
            base.AfterSolveInstance();
            if (this._queue.Count > 0)
            {
                this.CreatePromptPanel();
            }
            Grasshopper.Instances.RedrawCanvas();

        }

        public void SetInitCode(string code)
        {
            this._queue.Enqueue(code);
            GH_Panel panel = new();
            this.Params.Input[0].AddVolatileData(new Grasshopper.Kernel.Data.GH_Path(0), 0, code);
        }

        public void CreatePromptPanel()
        {
            string code = (string)this._queue.Dequeue();
            var pivot = new System.Drawing.PointF(this.Attributes.Pivot.X - 250, this.Attributes.Pivot.Y - 50);
            this.CreatePanel(code, "GHPT Prompt", pivot);
        }

        public void CreateAdvicePanel(string advice)
        {
            var pivot = new System.Drawing.PointF(this.Attributes.Pivot.X, this.Attributes.Pivot.Y - 250);
            this.CreatePanel(advice, "Advice", pivot, System.Drawing.Color.LightBlue);
        }

        public void CreatePanel(string content, string nickName, System.Drawing.PointF pivot)
        {
            this.CreatePanel(content, nickName, pivot, System.Drawing.Color.FromArgb(255, 255, 250, 90));
        }

        public void CreatePanel(string content, string nickName, System.Drawing.PointF pivot, System.Drawing.Color color)
        {
            GH_Panel panel = new();
            panel.NickName = nickName;

            panel.UserText = content;

            panel.Properties.Colour = color;
            //panel.AddVolatileData(new Grasshopper.Kernel.Data.GH_Path(0), 0, code);

            _doc.AddObject(panel, false);
            panel.Attributes.Pivot = pivot;
        }

        /// <summary>
        /// Provides an Icon for every component that will be visible in the User Interface.
        /// Icons need to be 24x24 pixels.
        /// You can add image files to your project resources and access them like this:
        /// return Resources.IconForThisComponent;
        /// </summary>
        protected override System.Drawing.Bitmap Icon => ConfigUtil.CurrentConfig.Version switch
        {
            GPTVersion.GPT3_5 => Resources.Icons.light_logo_gpt3_5_24x24,
            GPTVersion.GPT4 => Resources.Icons.light_logo_gpt4_24x24,
            _ => Resources.Icons.light_logo_24x24,
        };

        /// <summary>
        /// Each component must have a unique Guid to identify it. 
        /// It is vital this Guid doesn't change otherwise old ghx files 
        /// that use the old ID will partially fail during loading.
        /// </summary>
        public override Guid ComponentGuid => new("ea3a2f90-b8b9-406f-bb66-f2a4b9fa3812");
    }
}