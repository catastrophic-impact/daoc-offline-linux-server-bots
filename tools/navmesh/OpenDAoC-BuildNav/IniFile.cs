using System;
using System.Collections.Generic;
using System.IO;

namespace CEM.Client {
  /// <summary>
  /// INI Streamer Supports Reading and Writing of INI files Simple: Use GetItem/SetItem Advanced: Use Topic, GetTopic,
  /// Header, .. ©2006 Metty
  /// </summary>
  internal class IniFile {
    /// <summary>
    /// Initializes a new blank INI Streamer
    /// </summary>
    /// <param name="file"> </param>
    public IniFile(Stream stream) {
      Header = new List<string>();
      Header.Add("###########");
      Header.Add("Config File");
      Header.Add("Do not edit");
      Header.Add("###########");
      ReadIni(stream);
    }

    /// <summary>
    /// Retrieves a topic
    /// </summary>
    /// <param name="name"></param>
    /// <returns></returns>
    public Topic this[string name] {
      get { return _topics.ContainsKey(name) ? _topics[name] : null; }
    }

    #region Header

    /// <summary>
    /// Header List Contains Comments, which stand at the beginning of the file
    /// </summary>
    public List<string> Header { get; private set; }

    #endregion

    #region Topics

    private readonly Dictionary<string, Topic> _topics = new Dictionary<string, Topic>();

    /// <summary>
    /// Contains all topics of the ini file
    /// </summary>
    public IEnumerable<Topic> Topics {
      get { return _topics.Values; }
    }

    /// <summary>
    /// Represents a Topic object
    /// </summary>
    public class Topic {
      /// <summary>
      /// Creates a new Topic
      /// </summary>
      /// <param name="name"> </param>
      public Topic(string name) {
        Name = name;
        Items = new Dictionary<string, string>();
      }

      /// <summary>
      /// Gets an item, or returns null
      /// </summary>
      /// <param name="name"></param>
      /// <returns></returns>
      public string this[string name] {
        get { return Items.ContainsKey(name) ? Items[name] : null; }
      }

      /// <summary>
      /// Name of the Topic
      /// </summary>
      public string Name { get; private set; }

      /// <summary>
      /// Items in the topic
      /// </summary>
      public Dictionary<string, string> Items { get; private set; }
    }

    #endregion

    #region Write/Read Ini

    /// <summary>
    /// Rewrites the INI file
    /// </summary>
    /// <returns> Success </returns>
    public void WriteIni(Stream stream) {
      if (stream == null)
        throw new ArgumentNullException("stream");

      using (var writer = new StreamWriter(stream)) {
        foreach (var l in Header) writer.WriteLine("; " + l);
        foreach (var topic in Topics) {
          writer.WriteLine("");

          writer.WriteLine("[{0}]", topic.Name);

          foreach (var entry in topic.Items)
            writer.WriteLine("{0}={1}", entry.Key, entry.Value);
        }
        writer.Flush();
      }
    }

    /// <summary>
    /// (Re)Reads the INI File
    /// </summary>
    public void ReadIni(Stream stream) {
      if (stream == null)
        throw new ArgumentNullException("stream");
      var reader = new StreamReader(stream);

      Header.Clear();
      _topics.Clear();

      //Lets read it!
      Topic lasttopic = null;
      string line;

      char[] topicsep = {'[', ']'};
      char[] itemsep = {'='};

      while ((line = reader.ReadLine()) != null) {
        //Ignore comments
        if (line.StartsWith(";")) {
          if (lasttopic == null) if (line.Length >= 2) Header.Add(line.Remove(0, 2));
          continue;
        }
        if (line.StartsWith("#")) continue;
        //Ignore emptyl ines
        if (line == "") continue;

        if (line.StartsWith("[")) // Its a topic?
        {
          string[] split = line.Split(topicsep, 3); //3 - before [, between, and after ]

          //As there is a new topic, change the topic element!
          lasttopic = new Topic(split[1]);

          try {
            _topics.Add(lasttopic.Name, lasttopic);
          }
          catch {
            lasttopic = _topics[lasttopic.Name];
          } //duplicate entry?
        }
        else //Then its a item!
        {
          string[] split = line.Split(itemsep, 2);

          if (split.Length < 2) //error!
            continue;

          try {
            lasttopic.Items.Add(split[0], split[1]);
          }
          catch {} //duplicate entry?
        }
      }
      reader.Close();
      reader.Dispose();
    }

    /// <summary>
    /// Clears the current memory Ini file
    /// </summary>
    public void ClearIni() {
      Header.Clear();
      _topics.Clear();
    }

    #endregion

    #region Get/Set/Rem

    #region GetItem(s)

    /// <summary>
    /// Gets all items of the specified topic
    /// </summary>
    /// <param name="topic"> The Topic </param>
    /// <returns> List of Items </returns>
    public IEnumerable<string> GetItems(string topic) {
      return _topics[topic].Items.Keys;
    }

    /// <summary>
    /// Gets the specified item or creates it if neccessary
    /// </summary>
    /// <param name="topic"> </param>
    /// <param name="item"> </param>
    /// <returns> </returns>
    public string GetItem(string topic, string item) {
      return _topics[topic].Items[item];
    }

    #endregion

    #region AddTopic

    /// <summary>
    /// Adds a new topic to the INI file
    /// </summary>
    /// <param name="name"> Topic Name </param>
    public Topic AddTopic(string name) {
      return _topics[name] = new Topic(name);
    }

    #endregion

    #region SetItem

    /// <summary>
    /// Adds an item to the specified topic in the INI file
    /// </summary>
    /// <param name="topic"> Topic </param>
    /// <param name="item"> Item </param>
    /// <param name="content"> Content of the Item </param>
    public void SetItem(string topic, string item, string content) {
      if (!_topics.ContainsKey(topic))
        AddTopic(topic);
      _topics[topic].Items[item] = content;
    }

    #endregion

    #region RemTopic   

    /// <summary>
    /// Removes a topic from the INI file
    /// </summary>
    /// <param name="topic"> Topic to remove </param>
    public void RemTopic(string topic) {
      if (_topics.ContainsKey(topic)) _topics.Remove(topic);
    }

    #endregion

    #region RemItem

    /// <summary>
    /// Removes an Item from a topic
    /// </summary>
    /// <param name="topic"> Topic </param>
    /// <param name="item"> Item to remove </param>
    public void RemItem(string topic, string item) {
      Topic tpc = _topics[topic];
      if (tpc.Items.ContainsKey(item)) tpc.Items.Remove(item);
    }

    #endregion

    #endregion
  }
}