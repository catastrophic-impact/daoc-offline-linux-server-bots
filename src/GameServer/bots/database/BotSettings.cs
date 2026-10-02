using DOL.Database;
using DOL.Database.Attributes;

namespace DOL.GS.Database
{
    /// <summary>
    /// Database entity for bot behavioral settings
    /// </summary>
    [DataTable(TableName = "bot_settings")]
    public class BotSettings : DataObject
    {
        private long m_botId;
        private short m_followDistance;
        private string m_combatMode;
        private byte m_healThreshold;
        private string m_preferredTarget;

        public BotSettings()
        {
            AllowAdd = true;
            AllowDelete = true;
        }

        /// <summary>
        /// Foreign key to bot_profiles.bot_id
        /// </summary>
        [PrimaryKey]
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
        /// Follow distance in units
        /// </summary>
        [DataElement(AllowDbNull = false)]
        public short FollowDistance
        {
            get { return m_followDistance; }
            set
            {
                Dirty = true;
                m_followDistance = value;
            }
        }

        /// <summary>
        /// Combat mode setting
        /// </summary>
        [DataElement(AllowDbNull = false, Varchar = 32)]
        public string CombatMode
        {
            get { return m_combatMode; }
            set
            {
                Dirty = true;
                m_combatMode = value;
            }
        }

        /// <summary>
        /// Heal threshold percentage
        /// </summary>
        [DataElement(AllowDbNull = false)]
        public byte HealThreshold
        {
            get { return m_healThreshold; }
            set
            {
                Dirty = true;
                m_healThreshold = value;
            }
        }

        /// <summary>
        /// Preferred target selection
        /// </summary>
        [DataElement(AllowDbNull = false, Varchar = 32)]
        public string PreferredTarget
        {
            get { return m_preferredTarget; }
            set
            {
                Dirty = true;
                m_preferredTarget = value;
            }
        }
    }
}