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
        // how many seconds should be buffered by LSL; low value to ensure real-time, high to limit data loss. At least 1 second.
        [Input("Max Buffer Length", DefaultValue = 1, IsSingle = true)]
        public ISpread<int> FMaxBufLen;

        [Input("StreamName", IsSingle = true)]
		public IDiffSpread<string> FResourceName;

        //A spread of Stream types
        public Spread<IIOContainer<ISpread<string>>> FResourceType = new Spread<IIOContainer<ISpread<string>>>();
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
                FResourceType,
                (i) =>
                {
                    var ioAttribute = new InputAttribute(string.Format("Stream type {0}", i));
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
            for (int i = 0; i < FNbChannels.SliceCount; ++i)
            {
                FNbChannels[i].IOObject.Changed += HandleNbChannelChanged;
            }

            ////Create streams with current channel numbers
            //UpdateStreams();
        }

        private void HandleNbChannelChanged(IDiffSpread<int> sender)
        {
            UpdateStreams();
        }

        private void UpdateStreams()
        {
            //Collect the channel count for each stream
            int[] nbChannel = new int[FNbChannels.SliceCount];
            for (int i = 0; i < FNbChannels.SliceCount; ++i)
                nbChannel[i] = (FNbChannels[i].IOObject[0]);

            //Collect the stream type names
            string[] names = new string[FResourceType.SliceCount];
            for (int i = 0; i < FResourceType.SliceCount; ++i)
                names[i] = FResourceType[i].IOObject[0];

            //Make sure no stream type or stream name is empty


            //Initialize the streams
            InitializeStreams(FResourceName[0], names, nbChannel);
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
