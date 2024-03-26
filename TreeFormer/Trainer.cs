using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Metadata;
using System.Runtime.CompilerServices;
using System.Runtime.Intrinsics;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Transactions;
using System.Xml.Linq;

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
		public static int ComputeStaticLookback(IEnumerable<(Node node, bool mode)> enumerable){
			int lb = 0;
			foreach((Node node, bool mode) in enumerable){
				if(mode){
					if(node.dynamic_lookback){
						lb = -1;
					} else if(node.relative_lookback & lb > -1){
						lb += node.lookback;
					} else{
						lb = node.lookback;
					}
				}
			}
			return lb;
		}
		public static IEnumerable<Node> InventStaticNodes(State inspire, int static_lookback_limit, int relative_lookback_limit, int static_lookback) {
			int len = inspire.prevs.Length;
			


			//Static absolute lookback
			for (int i = 0; i < Math.Min(static_lookback_limit, len); ++i) {
				yield return new Node() { lookback = i, compare = inspire.prevs.Span[(len - i) - 1] };
			}


			//Static relative lookback
			int lookback_start = 1 - Math.Min(len - inspire.lookback, relative_lookback_limit);
			int lookback_end = Math.Min(inspire.lookback, relative_lookback_limit) + 1;
			if (static_lookback > -1)
			{
				lookback_end = Math.Min(lookback_end + static_lookback, static_lookback_limit) - static_lookback;
			}
			while (lookback_start < lookback_end)
			{
				yield return new Node() { relative_lookback = true, lookback = -lookback_start, compare = inspire.prevs.Span[((len - (inspire.lookback - lookback_start)) - 1)] };
				++lookback_start;
			}
		}
		public static IEnumerable<Node> InventDynamicNodes(State inspire){
			//Dynamic relative lookback
			Dictionary<ushort, bool> keyValuePairs = new Dictionary<ushort, bool>();
			for (int i = 0, len = inspire.prevs.Length; i < len; ++i)
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
		
		public static (Node? node, double information_gain) FindOptimalNode(IEnumerable<State> states, int static_lookback_limit, int relative_lookback_limit, int minimum_split_treshold, int static_optimal_search_max_iterations, int dynamic_optimal_search_max_iterations, int static_lookback, bool allowDynamicLookback, int stochastic_discover_limit, int stochastic_search_limit, double min_information_gain)
		{
			State[] statesArr = states.ToArray();
			int ctr = statesArr.Length;
			if(ctr < minimum_split_treshold){
				return (null,0.0);
			}
			if(ctr > stochastic_search_limit | ctr > stochastic_discover_limit){
				Array.Sort(statesArr, new StateComparer());
			}
			stochastic_discover_limit = Math.Min(stochastic_discover_limit, ctr);
			stochastic_search_limit = Math.Min(stochastic_search_limit, ctr);

			Dictionary<ushort, ulong[]> keyValuePairs1 = new Dictionary<ushort, ulong[]>();

			Dictionary<Node, ulong[]> keyValuePairs = new();
			Dictionary<Node, ulong[]>? dynamicKeyValuePairs = allowDynamicLookback ? new() : null;

			for (int i = 0; i < stochastic_discover_limit; ){
				State state = statesArr[i++];
				IncrInternal(keyValuePairs1, state.trueClass);
				foreach (Node node in InventStaticNodes(state, static_lookback_limit, relative_lookback_limit, static_lookback))
				{
					IncrInternal(keyValuePairs, node);
				}
				if(dynamicKeyValuePairs is { }){
					foreach (Node node in InventDynamicNodes(state))
					{
						IncrInternal(dynamicKeyValuePairs, node);
					}
				}
			}
			double dctr = stochastic_search_limit;
			double entropy = ComputeEntropyInternal(keyValuePairs1.Values, dctr);

			int nodecount = keyValuePairs.Count;
			StaticDistanceComparer staticDistanceComparer = new StaticDistanceComparer(stochastic_search_limit / 2);
			Node? bestNode = null;
			double highscore = min_information_gain;
			if (dynamicKeyValuePairs is { })
			{
				int z1 = 0;
				int dnodecount = dynamicKeyValuePairs.Count;
				KeyValuePair<Node, ulong[]>[] dnodes = new KeyValuePair<Node, ulong[]>[dnodecount];
				foreach (KeyValuePair<Node, ulong[]> keyValuePair in dynamicKeyValuePairs)
				{
					dnodes[z1++] = keyValuePair;
				}
				Array.Sort(dnodes, staticDistanceComparer);
				for (int i = 0, stop = Math.Min(dnodecount, dynamic_optimal_search_max_iterations); i < stop; ++i)
				{


					Node node = dnodes[i].Key;

					ulong ca = 0;
					ulong cb = 0;
					Dictionary<ushort, ulong[]> a = new Dictionary<ushort, ulong[]>();
					Dictionary<ushort, ulong[]> b = new Dictionary<ushort, ulong[]>();
					for (int i2 = 0; i2 < stochastic_search_limit; )
					{
						State s = statesArr[i2++];
						ushort trueClass = s.trueClass;
						Dictionary<ushort, ulong[]> c;
						if (node.EvaluateMeOnly(ref s))
						{
							++ca;
							c = a;
						}
						else
						{
							++cb;
							c = b;
						}
						IncrInternal(c, trueClass);
					}
					if (ca == 0 | cb == 0)
					{
						continue;
					}

					double information_gain = ((ComputeEntropyInternal(a.Values, ca) * (ca / dctr)) + (ComputeEntropyInternal(b.Values, cb) * (cb / dctr))) - entropy;


					if (information_gain > highscore)
					{
						highscore = information_gain;
						bestNode = node;
					}
					//logDrain.Write("Information gain: " + highscore);

				}
			}

			int z = 0;
			KeyValuePair<Node, ulong[]>[] nodes = new KeyValuePair<Node, ulong[]>[nodecount];
			foreach(KeyValuePair<Node, ulong[]> keyValuePair in keyValuePairs){
				nodes[z++] = keyValuePair;
			}
			
			Array.Sort(nodes, staticDistanceComparer);
			


			


			for (int i = 0, stop = Math.Min(nodecount, static_optimal_search_max_iterations); i < stop; ++i){
				
				
				Node node = nodes[i].Key;

				ulong ca = 0;
				ulong cb = 0;
				Dictionary<ushort, ulong[]> a = new Dictionary<ushort, ulong[]>();
				Dictionary<ushort, ulong[]> b = new Dictionary<ushort, ulong[]>();
				for (int i2 = 0; i2 < stochastic_search_limit;)
				{
					State s = statesArr[i2++];
					ushort trueClass = s.trueClass;
					Dictionary<ushort, ulong[]> c;
					if (node.EvaluateMeOnly(ref s))
					{
						++ca;
						c = a;
					}
					else
					{
						++cb;
						c = b;
					}
					IncrInternal(c, trueClass);
				}
				if (ca == 0 | cb == 0)
				{
					continue;
				}

				double information_gain = ((ComputeEntropyInternal(a.Values, ca) * (ca / dctr)) + (ComputeEntropyInternal(b.Values, cb) * (cb / dctr))) - entropy;

				if (information_gain > highscore)
				{
					highscore = information_gain;
					bestNode = node;
				}
				//logDrain.Write("Information gain: " + highscore);

			}
			
			return (bestNode,bestNode is null ? 0 : highscore);
			
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
		/*
		private static Task<(Node?,double)> SplitNodeImpl2Async(Node node, bool mode, IEnumerable<State> train_dataset, int static_lookback_limit, int relative_lookback_limit, int static_optimal_search_max_iterations, ulong minimum_split_treshold, int dynamic_optimal_search_max_iterations, bool allowDynamicLookback)
		{
			var tsk = new Task<(Node?,double)>(() => SplitNodeImpl2(node, mode, train_dataset, static_lookback_limit, relative_lookback_limit, static_optimal_search_max_iterations, minimum_split_treshold, dynamic_optimal_search_max_iterations, allowDynamicLookback),TaskCreationOptions.LongRunning);
			tsk.Start();
			return tsk;
		}
		private static async Task SplitNodeImpl(Node node, bool mode, IEnumerable<State> train_dataset, int static_lookback_limit, int relative_lookback_limit, int static_optimal_search_max_iterations, ulong minimum_split_treshold, int dynamic_optimal_search_max_iterations, ulong minDynamicLookbackDepth, ILogDrain logDrain)
		{
			(Node? thenode, double information_gain) = await SplitNodeImpl2Async(node, mode, train_dataset, static_lookback_limit, relative_lookback_limit, static_optimal_search_max_iterations, minimum_split_treshold, dynamic_optimal_search_max_iterations, minDynamicLookbackDepth == 0);
			if(thenode is { }){
				logDrain.Write("Added new node! Information gain: " + information_gain);
				if (minDynamicLookbackDepth > 0)
				{
					--minDynamicLookbackDepth;
				}
				await SplitNodeImpl3(thenode, train_dataset, static_lookback_limit, relative_lookback_limit, static_optimal_search_max_iterations, minimum_split_treshold, dynamic_optimal_search_max_iterations, minDynamicLookbackDepth, logDrain);
			}
		}
		private static Task SplitNodeImpl3(Node node, IEnumerable<State> train_dataset, int static_lookback_limit, int relative_lookback_limit, int static_optimal_search_max_iterations, ulong minimum_split_treshold, int dynamic_optimal_search_max_iterations, ulong minDynamicLookbackDepth, ILogDrain logDrain)
		{
			return Task.WhenAll(SplitNodeImpl(node, true, train_dataset, static_lookback_limit, relative_lookback_limit, static_optimal_search_max_iterations, minimum_split_treshold, dynamic_optimal_search_max_iterations, minDynamicLookbackDepth, logDrain), SplitNodeImpl(node, false, train_dataset, static_lookback_limit, relative_lookback_limit, static_optimal_search_max_iterations, minimum_split_treshold, dynamic_optimal_search_max_iterations, minDynamicLookbackDepth, logDrain));
		}
		
		private static (Node? temp, double information_gain) SplitNodeImpl2(Node node, bool mode, IEnumerable<State> train_dataset, int static_lookback_limit, int relative_lookback_limit, int static_optimal_search_max_iterations, ulong minimum_split_treshold, int dynamic_optimal_search_max_iterations, bool allowDynamicLookback){
			(Node, bool)[] nodes = node.TraceBackward(mode).ToArray();
			Array.Reverse(nodes);
			(Node? temp, double information_gain) = FindOptimalNode(MultiEval(nodes, train_dataset), static_lookback_limit, relative_lookback_limit, minimum_split_treshold, static_optimal_search_max_iterations, dynamic_optimal_search_max_iterations, ComputeStaticLookback(nodes), allowDynamicLookback);
			if (temp is { })
			{
				temp.parent = node;
				temp.parent_mode = mode;
				if(mode){
					node.child_true = temp;
				} else{
					node.child_false = temp;
				}
			}
			return (temp, information_gain);
		}

		public static async Task<Node?> Train(IEnumerable<State> train_dataset, IEnumerable<State> eval_dataset, int static_lookback_limit, int relative_lookback_limit, int static_optimal_search_max_iterations, ulong minimum_split_treshold, int dynamic_optimal_search_max_iterations, ulong minDynamicLookbackDepth, ILogDrain logDrain)
		{
			logDrain.Write("Finding bootstrap split...");
			(Node? root, _) = FindOptimalNode(train_dataset, static_lookback_limit, relative_lookback_limit, minimum_split_treshold, static_optimal_search_max_iterations, dynamic_optimal_search_max_iterations, 0, minDynamicLookbackDepth == 0);
			if(root is null){
				logDrain.Write("Not enough data to train model!");
				return null;
			}
			logDrain.Write("Training model...");
			await SplitNodeImpl3(root, train_dataset, static_lookback_limit, relative_lookback_limit, static_optimal_search_max_iterations, minimum_split_treshold, dynamic_optimal_search_max_iterations, minDynamicLookbackDepth, logDrain);
			
			
			logDrain.Write("Computing class probabilities (Step 1)...");
			Dictionary<Node, NodeCountState> keyValuePairs = new Dictionary<Node, NodeCountState>(ReferenceEqualityComparer.Instance);
			foreach (State state in eval_dataset)
			{
				State s = state;
				Node node = Execute(root, ref s, out bool mode);
				if(!keyValuePairs.TryGetValue(node, out NodeCountState s2)){
					s2 = new NodeCountState();
					keyValuePairs.Add(node, s2);
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
			foreach (KeyValuePair<Node, NodeCountState> keyValuePair in keyValuePairs)
			{
				Node node = keyValuePair.Key;
				NodeCountState s2 = keyValuePair.Value;
				node.classProbs_false = ComputeClassProbabilitiesInternal(s2.false_ctr, s2.total_false_ctr);
				node.classProbs_true = ComputeClassProbabilitiesInternal(s2.true_ctr, s2.total_true_ctr);
			}

			return root;
		}
		*/
		public static Node? TrainLegacy(IEnumerable<State> train_dataset, IEnumerable<State> eval_dataset, int static_lookback_limit, int relative_lookback_limit, int static_optimal_search_max_iterations, int minimum_split_treshold, int dynamic_optimal_search_max_iterations, int minDynamicLookbackDepth, int stochastic_discover_limit, int stochastic_search_limit, int max_depth, double min_information_gain, ILogDrain logDrain)
		{
			logDrain.Write("Finding bootstrap split...");
			(Node? root, double ig) = FindOptimalNode(train_dataset, static_lookback_limit, relative_lookback_limit, minimum_split_treshold, static_optimal_search_max_iterations, dynamic_optimal_search_max_iterations, 0, minDynamicLookbackDepth == 0, stochastic_discover_limit, stochastic_search_limit, min_information_gain);
			if (root is null)
			{
				logDrain.Write("Not enough data to train model!");
				return null;
			}
			logDrain.Write("Bootstrap split information gain: " + ig + "!");
			logDrain.Write("Training model...");
			Queue<Node> queue = new Queue<Node>();

			queue.Enqueue(root);
			while (queue.TryDequeue(out Node node))
			{
				logDrain.Write("Splittable nodes remaining: " + (queue.Count + 1));
				(Node, bool)[] nodes = node.TraceBackward(false).ToArray();
				int depth = nodes.Length;
				bool can_expand = depth < max_depth;
				bool allowDynamicLookbacks = depth > minDynamicLookbackDepth;
				Array.Reverse(nodes);
				(Node? temp, ig) = FindOptimalNode(MultiEval(nodes, train_dataset), static_lookback_limit, relative_lookback_limit, minimum_split_treshold, static_optimal_search_max_iterations, dynamic_optimal_search_max_iterations, ComputeStaticLookback(nodes), allowDynamicLookbacks, stochastic_discover_limit, stochastic_search_limit, min_information_gain);
				if (temp is { })
				{
					logDrain.Write("Added new node! Information gain: " + ig);
					temp.parent = node;
					temp.parent_mode = false;
					node.child_false = temp;
					if(can_expand){
						queue.Enqueue(temp);
					}
				}
				nodes[nodes.Length - 1] = (node, true);
				(temp, ig) = FindOptimalNode(MultiEval(nodes, train_dataset), static_lookback_limit, relative_lookback_limit, minimum_split_treshold, static_optimal_search_max_iterations, dynamic_optimal_search_max_iterations, ComputeStaticLookback(nodes), allowDynamicLookbacks, stochastic_discover_limit, stochastic_search_limit, min_information_gain);
				if (temp is { })
				{
					logDrain.Write("Added new node! Information gain: " + ig);
					temp.parent = node;
					temp.parent_mode = true;
					node.child_true = temp;
					if (can_expand)
					{
						queue.Enqueue(temp);
					}
				}
			}
			object keyobj = new object();


			logDrain.Write("Computing class probabilities (Step 1)...");
			foreach (State state in eval_dataset)
			{
				State s = state;
				Node node = Execute(root, ref s, out bool mode);
				NodeCountState s2;
				if (node.extraData.TryGetValue(keyobj, out object obj))
				{
					s2 = (NodeCountState)obj;
				}
				else
				{
					s2 = new NodeCountState();
					node.extraData.Add(keyobj, s2);
				}


				Dictionary<ushort, ulong[]> d2;
				if (mode)
				{
					++s2.total_true_ctr;
					d2 = s2.true_ctr;
				}
				else
				{
					++s2.total_false_ctr;
					d2 = s2.false_ctr;
				}

				IncrInternal(d2, state.trueClass);
			}
			logDrain.Write("Computing class probabilities (Step 2)...");
			foreach (Node node in Misc.Flatten(root))
			{
				if (node.extraData.Remove(keyobj, out object obj))
				{
					NodeCountState s2 = (NodeCountState)obj;
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
