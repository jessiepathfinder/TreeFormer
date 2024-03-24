using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;

namespace TreeFormer
{
	public interface IInfiniteEnumerator<T>{
		public T Get();
	}

	public static class Trainer
	{
		public static Node Execute(Node node, ref State state, out bool mode) {
		start:
			mode = node.EvaluateMeOnly(ref state);
			Node? n1 = mode ? node.child_true : node.child_false;
			if (n1 is null) {
				return node;
			}
			node = n1;
			goto start;
		}
		public static IEnumerable<Node> InventNodes(State inspire, int static_lookback_limit, int relative_lookback_limit) {
			int len = inspire.prevs.Length;
			int lookback_start = 1-Math.Min(len - inspire.lookback, relative_lookback_limit);
			int lookback_end = Math.Min(inspire.lookback, relative_lookback_limit) + 1;


			//Static relative lookback
			while (lookback_start < lookback_end)
			{
				yield return new Node() { relative_lookback = true, lookback = -lookback_start, compare = inspire.prevs.Span[((len - (inspire.lookback - lookback_start)) - 1)] };
				++lookback_start;
			}


			//Static absolute lookback
			for (int i = 0; i < Math.Min(static_lookback_limit, len); ++i) {
				yield return new Node() { lookback = i, compare = inspire.prevs.Span[(len - i) - 1] };
			}

			//Dynamic relative lookback
			Dictionary<ushort, bool> keyValuePairs = new Dictionary<ushort, bool>();
			--len;
			for (int i = 0; i < len; ++i)
			{
				ushort v = inspire.prevs.Span[i];
				if (keyValuePairs.TryAdd(v, false))
				{
					yield return new Node() { dynamic_lookback = true, compare = v };
				}
			}

		}
		private static double ComputeEntropyInternal(IEnumerable<ulong[]> dict, double count)
		{
			double sum = 0;
			foreach (ulong[] ulongs in dict) {
				double x = ulongs[0]/ count;
				if(x <= 0){
					continue;
				}

				sum += (x * Math.Log(x));
			}
			return sum;
		}
		private static void IncrInternal<T>(Dictionary<T, ulong[]> keyValuePairs, T key) where T : notnull {
			if (!keyValuePairs.TryGetValue(key, out ulong[] ulongs)) {
				ulongs = new ulong[1] { 1 };
				keyValuePairs.Add(key, ulongs);
			}
			++ulongs[0];
		}

		private sealed class ExtraDataNote{
			public ulong[]? bloomFilter;
			public bool inactive;
		}
		private sealed class StaticDistanceComparer : IComparer<KeyValuePair<Node, ulong[]>>
		{
			private readonly long basedist;

			public StaticDistanceComparer(long basedist)
			{
				this.basedist = basedist;
			}

			public int Compare(KeyValuePair<Node, ulong[]> x, KeyValuePair<Node, ulong[]> y)
			{
				return long.Sign(Math.Abs(basedist - (long)x.Value[0]) - Math.Abs(basedist - (long)y.Value[0]));
			}
		}

		public static Node? FindOptimalNode(IEnumerable<State> states, int static_lookback_limit, int relative_lookback_limit, ulong minimum_split_treshold, int optimal_search_max_iterations, ILogDrain logDrain)
		{
			foreach(State state in states){
				if(minimum_split_treshold == 0){
					break;
				}
				--minimum_split_treshold;
			}
			if(minimum_split_treshold > 0){
				return null;
			}
			Dictionary<ushort, ulong[]> keyValuePairs1 = new Dictionary<ushort, ulong[]>();

			Dictionary<Node, ulong[]> keyValuePairs = new();
			ulong ctr = 0;

			foreach(State state in states){
				IncrInternal(keyValuePairs1, state.trueClass);
				foreach (Node node in InventNodes(state, static_lookback_limit, relative_lookback_limit))
				{
					IncrInternal(keyValuePairs, node);
				}
				++ctr;
			}
			double dctr = ctr;
			double entropy = ComputeEntropyInternal(keyValuePairs1.Values, dctr);

			int nodecount = keyValuePairs.Count;
			int z = 0;
			KeyValuePair<Node, ulong[]>[] nodes = new KeyValuePair<Node, ulong[]>[nodecount];
			foreach(KeyValuePair<Node, ulong[]> keyValuePair in keyValuePairs){
				nodes[z++] = keyValuePair;
			}
			Array.Sort(nodes, new StaticDistanceComparer((long)(ctr / 2)));


			Node? bestNode = null;
			double highscore = 0;

			for (int i = 0, stop = Math.Min(nodecount, optimal_search_max_iterations); i < stop; ++i){
				
				
				Node node = nodes[i].Key;

				ulong ca = 0;
				ulong cb = 0;
				Dictionary<ushort, ulong[]> a = new Dictionary<ushort, ulong[]>();
				Dictionary<ushort, ulong[]> b = new Dictionary<ushort, ulong[]>();
				foreach (State state in states) {
					State s = state;
					Dictionary<ushort, ulong[]> c;
					if(node.EvaluateMeOnly(ref s)){
						++ca;
						c = a;
					} else{
						++cb;
						c = b;
					}
					IncrInternal(c, state.trueClass);
				}
				if (ca == 0 | cb == 0)
				{
					continue;
				}
				
				double information_gain = ((ComputeEntropyInternal(a.Values, ca) * (ca/dctr)) + (ComputeEntropyInternal(b.Values, cb)*(cb/dctr))) - entropy;
				if (information_gain > highscore)
				{
					highscore = information_gain;
					bestNode = node;
				}
				//logDrain.Write("Information gain: " + highscore);

			}
			return bestNode;
			
		}
		public static (bool result, State state) Eval(ReadOnlySpan<(Node node, bool mode)> trace, State state){
			foreach((Node node, bool mode) in trace){
				if(node.EvaluateMeOnly(ref state) != mode){
					return (false, default);
				}
			}
			return (true, state);
		}
		public static IEnumerable<State> MultiEval(ReadOnlyMemory<(Node node, bool mode)> trace, IEnumerable<State> states){
			foreach(State state in states){
				(bool result, State state1) = Eval(trace.Span, state);
				if(result){
					yield return state1;
				}
			}
		}
		private sealed class NodeCountState{
			public readonly Dictionary<ushort, ulong[]> true_ctr = new Dictionary<ushort, ulong[]>();
			public readonly Dictionary<ushort, ulong[]> false_ctr = new Dictionary<ushort, ulong[]>();
			public ulong total_true_ctr;
			public ulong total_false_ctr;
		}
		public static Node? Train(IEnumerable<State> train_dataset, IEnumerable<State> eval_dataset, int static_lookback_limit, int relative_lookback_limit, int optimal_search_max_iterations, ulong minimum_split_treshold, ILogDrain logDrain)
		{
			logDrain.Write("Finding bootstrap split...");
			Node? root = FindOptimalNode(train_dataset, static_lookback_limit, relative_lookback_limit, minimum_split_treshold, optimal_search_max_iterations, logDrain);
			if(root is null){
				logDrain.Write("Not enough data to train model!");
				return null;
			}
			logDrain.Write("Training model...");
			Queue<Node> queue = new Queue<Node>();
			
			queue.Enqueue(root);
			while(queue.TryDequeue(out Node node)){
				Console.WriteLine("Splittable nodes remaining: " + queue.Count);
				(Node,bool)[] nodes = node.TraceBackward(false).ToArray();
				Array.Reverse(nodes);
				Node? temp = FindOptimalNode(MultiEval(nodes, train_dataset), static_lookback_limit, relative_lookback_limit, minimum_split_treshold, optimal_search_max_iterations, logDrain);
				if(temp is {}){
					logDrain.Write("Added new node!");
					temp.parent = node;
					temp.parent_mode = false;
					node.child_false = temp;
					queue.Enqueue(temp);
				}
				nodes[nodes.Length - 1] = (node, true);
				temp = FindOptimalNode(MultiEval(nodes, train_dataset), static_lookback_limit, relative_lookback_limit, minimum_split_treshold, optimal_search_max_iterations, logDrain);
				if (temp is { })
				{
					logDrain.Write("Added new node!");
					temp.parent = node;
					temp.parent_mode = true;
					node.child_true = temp;
					queue.Enqueue(temp);
				}
			}
			object keyobj = new object();


			logDrain.Write("Computing class probabilities (Step 1)...");
			foreach(State state in eval_dataset)
			{
				State s = state;
				Node node = Execute(root, ref s, out bool mode);
				NodeCountState s2;
				if(node.extraData.TryGetValue(keyobj, out object obj)){
					s2 = (NodeCountState) obj;
				} else{
					s2 = new NodeCountState();
					node.extraData.Add(keyobj, s2);
				}
				

				Dictionary<ushort, ulong[]> d2;
				if(mode){
					++s2.total_true_ctr;
					d2 = s2.true_ctr;
				} else{
					++s2.total_false_ctr;
					d2 = s2.false_ctr;
				}

				IncrInternal(d2, state.trueClass);
			}
			logDrain.Write("Computing class probabilities (Step 2)...");
			foreach (Node node in Misc.Flatten(root))
			{
				if(node.extraData.Remove(keyobj, out object obj)){
					NodeCountState s2 = (NodeCountState) obj;
					node.classProbs_false = ComputeClassProbabilitiesInternal(s2.false_ctr, s2.total_false_ctr);
					node.classProbs_true = ComputeClassProbabilitiesInternal(s2.true_ctr, s2.total_true_ctr);

				}
			}

			return root;
		}
		private static Dictionary<string, double> ComputeClassProbabilitiesInternal(IReadOnlyDictionary<ushort, ulong[]> keyValuePairs, double divide){
			Dictionary<string, double> keyValuePairs1 = new Dictionary<string, double>(keyValuePairs.Count);
			foreach(KeyValuePair<ushort, ulong[]> keyValuePair in keyValuePairs){
				keyValuePairs1.Add(keyValuePair.Key.ToString(), keyValuePair.Value[0] / divide);
			}
			return keyValuePairs1;
		}
		
	}
}
