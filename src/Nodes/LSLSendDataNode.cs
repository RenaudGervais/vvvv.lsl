#region usings
using System;
using System.ComponentModel.Composition;

using VVVV.PluginInterfaces.V1;
using VVVV.PluginInterfaces.V2;
using VVVV.Utils.VColor;
using VVVV.Utils.VMath;

using VVVV.Core.Logging;

using LSL;

#endregion usings

namespace VVVV.Nodes
{
	
	//ISpread <double>
	#region PluginInfo
	[PluginInfo(Name = "LSLSendData", Category = "Value", Help = "Lab Streaming Layer sending protocol", Tags = "lsl, network")]
	#endregion PluginInfo
	
	public class LSLSendDataNode : IPluginEvaluate, IPartImportsSatisfiedNotification
	{
		[Import()]
		ILogger Flogger;
		
		#region fields & pins
		[Input("StreamType", DefaultString = "type", IsSingle = true)]
		public IDiffSpread<string> FResourceType;

        [Input("StreamName", DefaultString = "name")]
        public IDiffSpread<string> FResourceName;
        
        //The data pins
        public Spread<IIOContainer<ISpread<double>>> FData = new Spread<IIOContainer<ISpread<double>>>();
		
		// how many seconds should be buffered by LSL; low value to ensure real-time, high to limit data loss. At least 1 second.
		[Input("MaxBufLen", DefaultValue = 1, IsSingle = true)]
		public ISpread<int> FMaxBufLen;
		
		// leave to 0 to avoid lag, even if signal may get distored
		[Input("TimeOut", DefaultValue = 0, IsSingle = true)]
		public ISpread<double> FTimeOut;
		
		// will fetch at least that much samples per loop. If too low may miss values, if too high and can't keep up pace with server, may slow down computer
		// Should be a multiple of ChunkSize to ensure that the actual number of samples read reamain below.
		[Input("MaxSamples", DefaultValue = 512, IsSingle = true)]
		public ISpread<int> FMaxSamples;
		
		// chunk size for each pull, too low may slow down computer, too high... ?
		[Input("ChunkSize", DefaultValue = 32, IsSingle = true)]
		public ISpread<int> FChunkSize;
		
		// Manually ask the node to resovle the stream
        [Input("Find Stream", IsSingle = true, IsBang = true)]
        public ISpread<bool> FFindStream;

		// Spreads (channels) of spreads (chunks)
		[Output("Output")]
		public ISpread<ISpread<double>> FOutput;
		
	    [Output("NBChannels", IsSingle = true) ]
		public ISpread<int> FNBChannels;
		
		[Output("SampleRate", IsSingle = true) ]
		public ISpread<double> FSampleRate;

        [Import]
        public IIOFactory FIOFactory;
        #endregion fields & pins


        #region pin management
        public void OnImportsSatisfied()
        {
            //Register input pins event listeners
            FResourceName.Changed += HandleNbStreamChanged;
        }

        private void HandlePinCountChanged<T>(ISpread<int> countSpread, Spread<IIOContainer<T>> pinSpread, Func<int, IOAttribute> ioAttributeFactory) where T : class
        {
            pinSpread.ResizeAndDispose(
                countSpread[0],
                (i) =>
                {
                    var ioAttribute = ioAttributeFactory(i + 1);
                    return FIOFactory.CreateIOContainer<T>(ioAttribute);
                }
            );
        }

        private void HandleNbStreamChanged(IDiffSpread<string> sender)
        {
            Spread<int> nbSlice = new Spread<int>(FResourceName.SliceCount);

            //Create the pins for data
            HandlePinCountChanged(nbSlice, FData, (i) => new InputAttribute("Data " + FResourceName[i]));
        }
        #endregion pin management


        private liblsl.StreamInfo mInfo;
		private liblsl.StreamInlet mInlet;


        void InitializeStream(string type, string[] name)
        {
            //Create the stream info and outlet
            mInfo = new liblsl.StreamInfo()

            // wait until an EEG stream shows up
            results = liblsl.resolve_stream("type", FResourceType[0], 1, 1);

            Flogger.Log(LogType.Debug, "Number of streams: " + results.Length);

            for (int i = 0; i < results.Length; i++)
            {
                liblsl.StreamInlet tmpInlet = new liblsl.StreamInlet(results[i], FMaxBufLen[0]);
                liblsl.StreamInfo tmpInfo = tmpInlet.info();
                Flogger.Log(LogType.Debug, "Look at stream name: " + tmpInfo.name());
                if (FResourceName[0].Equals(tmpInfo.name()))
                {
                    Flogger.Log(LogType.Debug, "Bingo!");
                    // retrieve data
                    info = tmpInfo;
                    inlet = tmpInlet;
                    FNBChannels[0] = info.channel_count();
                    FSampleRate[0] = info.nominal_srate();
                    Flogger.Log(LogType.Debug, "Nb channels: " + info.channel_count());
                    Flogger.Log(LogType.Debug, "Sample rate: " + info.nominal_srate());
                    break;
                }
            }
        }
		
		//called when data for any output pin is requested
		public void Evaluate(int SpreadMax)
		{
            // Try to establish connexion when input stream name changed or if
            // manually triggered
            if (FResourceName.IsChanged || FResourceType.IsChanged || FFindStream[0])
                Connect();

            if (FNBChannels[0] > 0)
            {
				// First slices: channels
        		FOutput.SliceCount = FNBChannels[0];
				
				// VPRN tells us how many values we have and we know the size of a chunk: easy to compute how many channels we receive
       			//FNBChannels[0] = 1;// e.Channels.Length / FChunkSize[0];
        	
				// pull all we can
				int totalChunks = 0;
				float[,] sample = new float[FChunkSize[0],FNBChannels[0]];
				double[] timestamps = new double[FChunkSize[0]];
				int nbChunks = -1;
				//Flogger.Log(LogType.Debug,"new loop ");
				while(nbChunks != 0 && totalChunks < FMaxSamples[0] ) {
					nbChunks = inlet.pull_chunk( sample, timestamps, FTimeOut[0]);
					//Flogger.Log(LogType.Debug,"timestamp: " + nbChunks);
					totalChunks += nbChunks;
					
					// try to fill
					for (int chan = 0; chan < FNBChannels[0]; chan++) {
						// Within slices we have chunks
						FOutput[chan].SliceCount = totalChunks;
						for (int i = 0; i < nbChunks; i++) {
							// fill from the end
							FOutput[chan][totalChunks-nbChunks+i] = sample[i,chan];
						}
					}	
				}
				
            	//for (int chan = 0; chan < FNBChannels[0]; chan++) {
            	//	// Within slices we have chunks
				//	FOutput[chan].SliceCount = FChunkSize;
            	//	for (int i = 0; i < FChunkSize; i++) {
            	//		// We move to the correct position
            	//		int pos = chan * FChunkSize + i;
            	//		FOutput[chan][i] = 1;//e.Channels[pos];
            	//	}
		   		//}
			}
		}
	}
}
