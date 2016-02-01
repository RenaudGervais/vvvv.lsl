#region usings
using System;
using System.ComponentModel.Composition;
using System.Collections.Generic;

using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using VVVV.Utils.VColor;
using VVVV.Utils.VMath;

using VVVV.Core.Logging;

using LSL;

#endregion usings

namespace VVVV.Nodes
{
	
	#region PluginInfo
	[PluginInfo(Name = "LSLSendData", Category = "Value", Help = "Lab Streaming Layer sending protocol", Tags = "lsl, network")]
	#endregion PluginInfo
	
	public class LSLSendDataNode : IPluginEvaluate, IPartImportsSatisfiedNotification
	{
		[Import()]
		ILogger Flogger;
		
		#region fields & pins
		[Input("StreamName", IsSingle = true)]
		public IDiffSpread<string> FResourceName;

        [Input("StreamType")]
        public IDiffSpread<string> FResourceType;
        
        //The data pins
        public Spread<IIOContainer<ISpread<double>>> FData = new Spread<IIOContainer<ISpread<double>>>();

        public Spread<IIOContainer<IDiffSpread<int>>> FNbChannel = new Spread<IIOContainer<IDiffSpread<int>>>();
		
		// how many seconds should be buffered by LSL; low value to ensure real-time, high to limit data loss. At least 1 second.
		[Input("Max Buffer Length", DefaultValue = 1, IsSingle = true)]
		public ISpread<int> FMaxBufLen;

        [Output("Status")]
        public ISpread<string> FStatus;

        [Import]
        public IIOFactory FIOFactory;


        //Member variables
        private liblsl.StreamInfo[] mInfo;
        private liblsl.StreamOutlet[] mOutlet;
        private string mStatus;
        #endregion fields & pins


        #region pin management
        public void OnImportsSatisfied()
        {
            //Register input pins event listeners
            FResourceType.Changed += HandleNbStreamChanged;

            //Initialize Data pin count
            HandleNbStreamChanged(FResourceType);
        }

        private void HandlePinCountChanged<T>(ISpread<int> countSpread, Spread<IIOContainer<T>> pinSpread, Func<int, IOAttribute> ioAttributeFactory) where T : class
        {
            pinSpread.ResizeAndDispose(
                countSpread[0],
                (i) =>
                {
                    var ioAttribute = ioAttributeFactory(i);
                    var io = FIOFactory.CreateIOContainer<T>(ioAttribute);
                    return io;
                }
            );
        }

        private void HandleNbStreamChanged(IDiffSpread<string> sender)
        {
            Spread<int> nbSlice = new Spread<int>(new int[] { FResourceType.SliceCount });

            //Create the pins for data and channel count
            HandlePinCountChanged(nbSlice, FData, (i) => new InputAttribute("Data " + FResourceType[i].ToString()));
            HandlePinCountChanged(
                nbSlice,
                FNbChannel,
                (i) =>
                {
                    var ioAttribute = new InputAttribute("Nb Channel " + FResourceType[i].ToString());
                    ioAttribute.DefaultValue = 1;
                    return ioAttribute;
                }
                );

            //Register nb channel changed event
            for (int i = 0; i < nbSlice[0]; ++i)
            {
                FNbChannel[i].IOObject.Changed += HandleNbChannelChanged;
            }

            //Create streams with current channel numbers
            //UpdateStreams();
        }

        private void HandleNbChannelChanged(IDiffSpread<int> sender)
        {
            UpdateStreams();
        }

        private void UpdateStreams()
        {             
            //Collect the channel number for each stream
            List<int> nbChannel = new List<int>();
            for (int i = 0; i < FResourceType.SliceCount; ++i)
                nbChannel.Add(FNbChannel[i].IOObject[0]);

            //Reinitialize the stream
            string[] names = new string[FResourceType.SliceCount];
            for (int i = 0; i < FResourceType.SliceCount; ++i)
                names[i] = FResourceType[i];
            InitializeStreams(FResourceName[0], names, nbChannel.ToArray());
        }
        #endregion pin management


        void InitializeStreams(string name, string[] type, int[] nbChannel)
        {
            if (type.Length == nbChannel.Length)
            {
                int nbStream = type.Length;
                mInfo = new liblsl.StreamInfo[nbStream];
                mOutlet = new liblsl.StreamOutlet[nbStream];
                for (int i = 0; i < nbStream; ++i)
                {
                    mInfo[i] = new liblsl.StreamInfo(name, type[i], nbChannel[i]);
                    mOutlet[i] = new liblsl.StreamOutlet(mInfo[i]);
                }

                mStatus = "OK";
            }
            else
                mStatus = "Channel numbers for each stream is not specified correctly";
        }
		
		//called when data for any output pin is requested
		public void Evaluate(int SpreadMax)
		{
            FStatus.SliceCount = 1;
            FStatus[0] = mStatus;
		}
	}
}
