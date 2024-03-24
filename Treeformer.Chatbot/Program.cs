using Newtonsoft.Json;
using System.Collections.Generic;
using System.Security.Cryptography;
using TreeFormer;

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
		private static TokenProbability[] GetTopK(IReadOnlyDictionary<string, double> keyValuePairs){
			int len = keyValuePairs.Count;
			int i = 0;
			TokenProbability[] tokenProbabilities = new TokenProbability[len];
			foreach (KeyValuePair<string, double> keyValuePair in keyValuePairs)
			{
				tokenProbabilities[i++] = new TokenProbability(Convert.ToUInt16(keyValuePair.Key), keyValuePair.Value);
			}
			Array.Sort(tokenProbabilities, TopKComparer.instance);
			return tokenProbabilities;
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
			Node root = JsonConvert.DeserializeObject<Node>(File.ReadAllText(datadir + model + ".model"),new JsonSerializerSettings(){ MaxDepth=65536});

			Console.WriteLine("Preparing tree for inference...");
			Dictionary<Node, TokenProbability[]> truedict = new Dictionary<Node, TokenProbability[]>(ReferenceEqualityComparer.Instance);
			Dictionary<Node, TokenProbability[]> falsedict = new Dictionary<Node, TokenProbability[]>(ReferenceEqualityComparer.Instance);

			IEnumerable<Node> flattened = Misc.Flatten(root);
			Misc.FixTree(flattened);
			foreach(Node node in flattened){
				Dictionary<string, double> cp = node.classProbs_true;
				if (cp is { }){
					truedict.Add(node, GetTopK(cp));
					falsedict.Add(node, GetTopK(node.classProbs_false));
					node.classProbs_true = null;
					node.classProbs_false = null;
				}
			}

			ushort[] buffer = new ushort[maxContextSize];
			while(true){
				Console.Write("User: ");
				int x = Misc.Tokenize(optidict, buffer, Console.ReadLine(), maxlen, magicTokenClasses);
				buffer[x++] = 0; //[START_GPT]
				while(x<maxContextSize){
					State s = new State(((ReadOnlyMemory<ushort>)buffer).Slice(0, x), 0, 0);
					Node n = Trainer.Execute(root, ref s, out bool m);
					TokenProbability[] d = (m ? truedict : falsedict)[n];

					double rng = (RandomNumberGenerator.GetInt32(0, 16777216)/ 16777216.0) * 0.1;
					ushort b2 = 1;
					for (int i = 0, stop = d.Length; i < stop & rng > 0; ++i ){
						TokenProbability tokenProbability = d[i];
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