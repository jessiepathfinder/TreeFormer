using Newtonsoft.Json;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using TreeFormer;
using JsonSerializer = Newtonsoft.Json.JsonSerializer;

namespace Treeformer.Chatbot
{
	internal static class Program
	{
		private readonly struct TokenProbability{
			public readonly ushort token;
			public readonly double probability;

			public TokenProbability(ushort token, double probability)
			{
				this.token = token;
				this.probability = probability;
			}
		}
		private static TokenProbability[] GetTopK(double[] doubles){
			int len = doubles.Length;
			TokenProbability[] tokenProbabilities = new TokenProbability[len];
			for(int i = 0; i < len; ++i){
				tokenProbabilities[i] = new TokenProbability((ushort)i,doubles[i]);
			}
			Array.Sort(tokenProbabilities, TopKComparer.instance);
			return tokenProbabilities;
		}
		private static IReadOnlyDictionary<ushort, double> GetTopK2(IReadOnlyDictionary<string, double> keyValuePairs)
		{
			Dictionary<ushort, double> mydict = new Dictionary<ushort, double>(keyValuePairs.Count);
			foreach (KeyValuePair<string, double> keyValuePair in keyValuePairs)
			{
				mydict.Add(Convert.ToUInt16(keyValuePair.Key), keyValuePair.Value);
			}
			return mydict;
		}
		private sealed class TopKComparer : IComparer<TokenProbability>
		{
			public static readonly TopKComparer instance = new TopKComparer();
			private TopKComparer(){}
			public int Compare(TokenProbability x, TokenProbability y)
			{
				return -x.probability.CompareTo(y.probability);
			}
		}
		private static void Main(string[] args)
		{
			string datadir = args[0];
			string model = args[1];
			if (!datadir.EndsWith(Path.DirectorySeparatorChar))
			{
				datadir += Path.DirectorySeparatorChar;
			}
			Console.WriteLine("Loading dictionary...");
			IReadOnlyDictionary<string, ushort>? dict = JsonConvert.DeserializeObject<IReadOnlyDictionary<string, ushort>>(File.ReadAllText(datadir + "encoder.json"));
			if (dict is null)
			{
				Console.WriteLine("Null encoder dictionary");
				return;
			}
			int maxlen = 0;
			int tokenclasses = 0;
			foreach (KeyValuePair<string, ushort> keyValuePair in dict)
			{
				maxlen = Math.Max(maxlen, keyValuePair.Key.Length);
				int val = keyValuePair.Value;
				tokenclasses = Math.Max(val, tokenclasses);
			}

			Console.WriteLine("Optimizing dictionary...");
			IReadOnlyDictionary<string, OptimizedTokenizerEntry> optidict = Misc.OptimizeDictionary(dict);

			int maxContextSize;
			int magicTokenClasses;

			switch(model){
				case "nano-v1":
					maxContextSize = 2048;
					magicTokenClasses = 2;
					break;
				default:
					Console.WriteLine("Unknown model: " + model);
					return;
			}
			tokenclasses += magicTokenClasses + 1;
			string[] strings = new string[tokenclasses];
			foreach (KeyValuePair<string, ushort> keyValuePair in dict)
			{
				strings[keyValuePair.Value + magicTokenClasses] = keyValuePair.Key;
			}

			Console.WriteLine("Opening model file...");
			using StreamReader streamReader = new StreamReader(new FileStream(datadir + model + ".model", FileMode.Open, FileAccess.Read, FileShare.Read, 16777216, FileOptions.SequentialScan), Encoding.UTF8, false, -1, false);


			JsonSerializer jsonSerializer = new JsonSerializer();
			jsonSerializer.MaxDepth = int.MaxValue;
			jsonSerializer.MissingMemberHandling = MissingMemberHandling.Ignore;




			ushort[] buffer = new ushort[maxContextSize];
			while(true){
				Console.Write("User: ");
				int x = Misc.Tokenize(optidict, buffer, Console.ReadLine(), maxlen, magicTokenClasses);
				buffer[x++] = 0; //[START_GPT]
				Console.Write("Treeformer GPT:");
				while (x<maxContextSize){
					State s = new State(((ReadOnlyMemory<ushort>)buffer).Slice(0, x), 0, 0);
					double[] doubles = new double[tokenclasses];

					ulong ctr = 0;
					using(JsonReader jsonReader = new JsonTextReader(streamReader)){
						jsonReader.CloseInput = false;
						while (jsonReader.Read()){
							JsonToken jsonTokenType = jsonReader.TokenType;
							if (jsonTokenType == JsonToken.EndArray)
							{
								break;
							}
							if(jsonTokenType == JsonToken.StartArray) {
								continue;
							}
							++ctr;
							State s2 = s;
							Node node = Trainer.Execute(jsonSerializer.Deserialize<Node>(jsonReader), ref s2, out bool mode);
							foreach(KeyValuePair<string, double> keyValuePair in (mode ? node.classProbs_true : node.classProbs_false)){
								doubles[Convert.ToInt32(keyValuePair.Key)] += keyValuePair.Value;
							}
						}
						
						
					}
					streamReader.BaseStream.Position = 0;
					streamReader.DiscardBufferedData();

					

					double rng = (RandomNumberGenerator.GetInt32(0, 16777216)/ 16777216.0) * (ctr * 0.1);
					ushort b2 = 1;

					TokenProbability[] tokenProbabilities = GetTopK(doubles);
					for (int i = 0, stop = tokenProbabilities.Length; i < stop & rng > 0; ++i ){
						TokenProbability tokenProbability = tokenProbabilities[i];
						b2 = tokenProbability.token;
						rng -= tokenProbability.probability;
					}
					

					
					if(b2 == 1){
						break;
					}
					buffer[x++] = b2;
					Console.Write(strings[b2]);
				}
				Console.WriteLine();
			}
		}
	}
}