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
	[PluginInfo(Name = "LSLSendData", AutoEvaluate = true, Category = "Value", Help = "Lab Streaming Layer sending protocol", Tags = "lsl, network")]
	#endregion PluginInfo
	
	public class LSLSendDataNode : IPluginEvaluate, IPartImportsSatisfiedNotification
	{
		[Import()]
		ILogger Flogger;

        #region fields & pins
        //Sending data switch
        [Input("Send", IsToggle = true, IsSingle = true, DefaultBoolean = false)]
        public ISpread<bool> FSend;

        // how many seconds should be buffered by LSL; low value to ensure real-time, high to limit data loss. At least 1 second.
        [Input("Max Buffer Length", DefaultValue = 1, IsSingle = true)]
        public ISpread<int> FMaxBufLen;

        [Input("Stream Type", DefaultString = "stream", IsSingle = true, CheckIfChanged = true)]
		public IDiffSpread<string> FResourceType;

        //A spread of Stream types
        public Spread<IIOContainer<ISpread<string>>> FResourceName = new Spread<IIOContainer<ISpread<string>>>();
        //public IDiffSpread<string> FResourceType;

        //A spread of data
        public Spread<IIOContainer<ISpread<double>>> FData = new Spread<IIOContainer<ISpread<double>>>();

        //A spread of channel number associated with each stream type
        public Spread<IIOContainer<IDiffSpread<int>>> FNbChannels = new Spread<IIOContainer<IDiffSpread<int>>>();

        [Config("Stream type count", DefaultValue = 1, MinValue = 0)]
        public IDiffSpread<int> FResourceTypeCount;

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
            FResourceTypeCount.Changed += HandleNbStreamChanged;
            FResourceType.Changed += HandleStreamNameChanged;
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

        private void HandleNbStreamChanged(IDiffSpread<int> sender)
        {
            //Create the pins for data and channel count
            HandlePinCountChanged(
                sender,
                FResourceName,
                (i) =>
                {
                    var ioAttribute = new InputAttribute(string.Format("Stream Name {0}", i));
                    ioAttribute.IsSingle = true;
                    ioAttribute.DefaultString = ioAttribute.Name;
                    return ioAttribute;
                }
                );
            HandlePinCountChanged(
                sender,
                FNbChannels,
                (i) =>
                {
                    var ioAttribute = new InputAttribute(string.Format("Nb Channel {0}", i));
                    ioAttribute.DefaultValue = 1;
                    ioAttribute.MinValue = 1;
                    ioAttribute.IsSingle = true;
                    ioAttribute.CheckIfChanged = true;
                    return ioAttribute;
                }
                );
            HandlePinCountChanged(
                sender,
                FData,
                (i) =>
                {
                    var ioAttribute = new InputAttribute(string.Format("Data {0}", i));
                    ioAttribute.DefaultValue = 0;
                    return ioAttribute;
                }
                );

            //Register nb channel changed event
            FNbChannels.Sync();
            for (int i = 0; i < FNbChannels.SliceCount; ++i)
            {
                FNbChannels[i].IOObject.Changed += HandleNbChannelChanged;
            }

            //Create streams with current channel numbers
            //UpdateStreams();
        }

        private void HandleNbChannelChanged(IDiffSpread<int> sender)
        {
            UpdateStreams();
        }

        private void HandleStreamNameChanged(IDiffSpread<string> sender)
        {
            UpdateStreams();
        }

        private void UpdateStreams()
        {
            //Collect the channel count for each stream
            int[] nbChannel = new int[FResourceTypeCount[0]];
            for (int i = 0; i < nbChannel.Length; ++i)
            {
                if (FNbChannels[i].IOObject.SliceCount == 0) return;

                nbChannel[i] = FNbChannels[i].IOObject[0];
            }

            //Collect the stream type names
            string[] names = new string[FResourceName.SliceCount];
            bool emptyNames = false;
            for (int i = 0; i < FResourceName.SliceCount; ++i)
            {
                var types = FResourceName[i].IOObject;
                names[i] = types[0];
                emptyNames = emptyNames || string.IsNullOrEmpty(names[i]);
            }

            //Make sure no stream type or stream name is empty before initialization
            if (string.IsNullOrEmpty(FResourceType[0]) || emptyNames)
            {
                mStatus = "Stream names cannot be empty";
                return;
            }

            //Initialize the streams
            InitializeStreams(FResourceType[0], names, nbChannel);
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
                    mInfo[i] = new liblsl.StreamInfo(name, type[i], nbChannel[i], 100, liblsl.channel_format_t.cf_float32);
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
            if(FSend[0])
            {
                for( int pin = 0; pin < FResourceTypeCount[0]; ++pin)
                {
                    //var dataSpread = FNbChannels[pin].IOObject;
                    double[] data = new double[FNbChannels[pin].IOObject[0]];
                    for (int j = 0; j < data.Length; ++j)
                        data[j] = FData[pin].IOObject[j];
                    mOutlet[pin].push_sample(data);
                }
            }


            //Updating status message
            FStatus.SliceCount = 1;
            FStatus[0] = mStatus;
		}
	}
}
