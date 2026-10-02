using System;
using DOL.Database;
using DOL.Database.Attributes;

namespace DOL.GS.Database
{
    /// <summary>
    /// Database entity for bot profiles
    /// </summary>
    [DataTable(TableName = "bot_profiles")]
    public class BotProfile : DataObject
    {
        private long m_botId;
        private string m_ownerCharacterID;
        private string m_name;
        private byte m_classId;
        private byte m_raceId;
        private byte m_genderId;
        private byte m_level;
        private bool m_isActive;

        public BotProfile()
        {
            AllowAdd = true;
            AllowDelete = true;
        }

        /// <summary>
        /// Primary key for bot
        /// </summary>
        [PrimaryKey(AutoIncrement = true)]
        public long BotId
        {
            get { return m_botId; }
            set
            {
                Dirty = true;
                m_botId = value;
            }
        }

        /// <summary>
        /// Foreign key to character.ObjectId
        /// </summary>
        [DataElement(AllowDbNull = false, Index = true, Varchar = 255)]
        public string OwnerCharacterID
        {
            get { return m_ownerCharacterID; }
            set
            {
                Dirty = true;
                m_ownerCharacterID = value;
            }
        }

        /// <summary>
        /// Bot name
        /// </summary>
        [DataElement(AllowDbNull = false, Varchar = 64)]
        public string Name
        {
            get { return m_name; }
            set
            {
                Dirty = true;
                m_name = value;
            }
        }

        /// <summary>
        /// Class ID
        /// </summary>
        [DataElement(AllowDbNull = false)]
        public byte ClassId
        {
            get { return m_classId; }
            set
            {
                Dirty = true;
                m_classId = value;
            }
        }

        /// <summary>
        /// Race ID
        /// </summary>
        [DataElement(AllowDbNull = false)]
        public byte RaceId
        {
            get { return m_raceId; }
            set
            {
                Dirty = true;
                m_raceId = value;
            }
        }

        /// <summary>
        /// Gender ID
        /// </summary>
        [DataElement(AllowDbNull = false)]
        public byte GenderId
        {
            get { return m_genderId; }
            set
            {
                Dirty = true;
                m_genderId = value;
            }
        }

        /// <summary>
        /// Bot level
        /// </summary>
        [DataElement(AllowDbNull = false)]
        public byte Level
        {
            get { return m_level; }
            set
            {
                Dirty = true;
                m_level = value;
            }
        }

        /// <summary>
        /// Whether the bot is currently active
        /// </summary>
        [DataElement(AllowDbNull = false)]
        public bool IsActive
        {
            get { return m_isActive; }
            set
            {
                Dirty = true;
                m_isActive = value;
            }
        }
    }
}