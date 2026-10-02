using System.Collections.Generic;
using System.Linq;

namespace CEM.Datastructures
{
  /// <summary>
  /// Represents a simple, directional graph
  /// </summary>
  internal class Graph<T> {
    private readonly Dictionary<T, Node> _nodes = new Dictionary<T, Node>(); 

    /// <summary>
    /// Node valu
    /// </summary>
    public class Node {
      /// <summary>
      /// Directional connections to other nodes
      /// </summary>
      public List<Node> Connections { get; private set; }

      /// <summary>
      /// Value of this node
      /// </summary>
      public T Value { get; private set; }

      internal Node(T value) {
        Value = value;
        Connections = new List<Node>();
      }

      public override string ToString() {
        return string.Format("Node[{0}]", Value);
      }
    }

    /// <summary>
    /// Adds a new node to the graph
    /// </summary>
    public Node Add(T elem) {
      Node res;
      _nodes.Add(elem, res = new Node(elem));
      return res;
    } 

    /// <summary>
    /// Retrieves a node from the graph, or returns null if not found
    /// </summary>
    /// <param name="elem"></param>
    /// <returns></returns>
    public Node Get(T elem) {
      return _nodes.ContainsKey(elem) ? _nodes[elem] : null;
    }

    /// <summary>
    /// Adds a link from src to dest
    /// </summary>
    public void AddConnection(T src, T dest) {
      var srcNode = Get(src) ?? Add(src);
      var destNode = Get(dest) ?? Add(dest);
      srcNode.Connections.Add(destNode);
    }

    /// <summary>
    /// Calculates a path from the source to the destination.
    /// Returns null if no path was found.
    /// </summary>
    /// <param name="src"></param>
    /// <param name="dest"></param>
    /// <returns></returns>
    public T[] PlotPath(T src, T dest) {
      if (src.Equals(dest)) return new[] { dest };
      var srcNode = Get(src);
      var destNode = Get(dest);

      if (srcNode == null || destNode == null)
        return null;

      var toTest = new Queue<Node>();
      var reversePath = new Dictionary<Node, Node>();
      
      // Make a broad-search over the graph while preventing circles
      toTest.Enqueue(srcNode);
      while (toTest.Count > 0) {
        var cur = toTest.Dequeue();

        // Case 1) Yay, we found a path
        if (cur == destNode) { 
          // Walk back the path
          var path = new LinkedList<T>();
          do {
            path.AddFirst(cur.Value);
            cur = reversePath.ContainsKey(cur) ? reversePath[cur] : null;
          } while (cur != null);
          return path.ToArray();
        }

        // Case 2) Look at the children
        foreach (var child in cur.Connections) {
          if (reversePath.ContainsKey(child) || reversePath.ContainsValue(child))
            continue; // already (scheduled to be) tested
          reversePath.Add(child, cur);
          toTest.Enqueue(child);
        }
      }
      return null; // no way to get there
    } 
  }
}
