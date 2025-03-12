using System.Threading;
using System.Threading.Tasks;
using Robust.Shared.Network;
using Robust.Shared.Player;
using Robust.Shared.Asynchronous;
using Robust.Shared.Timing;
using Content.Server.Database;
using Robust.Server.Player;
using Content.Shared._Goobstation.ServerCurrency.Events;

namespace Content.Server._Goobstation.ServerCurrency
{
    public sealed class ServerCurrencyManager : IPostInjectInit
    {
        [Dependency] private readonly IServerDbManager _db = default!;
        [Dependency] private readonly UserDbDataManager _userDb = default!;
        [Dependency] private readonly ITaskManager _task = default!;
        [Dependency] private readonly IGameTiming _timing = default!;
        [Dependency] private readonly IPlayerManager _player = default!;
        private ISawmill _sawmill = default!;
        private Dictionary<NetUserId, BalanceData> _balances = new();
        private readonly List<Task> _pendingSaveTasks = new();
        private TimeSpan _saveInterval = TimeSpan.FromSeconds(600); // Yeah, players will only gain coins mid round from gifting/admins, so we dont need to update often.
        private TimeSpan _lastSave;
        public event Action<PlayerBalanceChangeEvent>? BalanceChange;

        public void Initialize()
        {
            _sawmill = Logger.GetSawmill("server_currency");
        }

        void IPostInjectInit.PostInject()
        {
            _userDb.AddOnLoadPlayer(PlayerConnected);
            _userDb.AddOnPlayerDisconnect(PlayerDisconnected);
        }

        /// <summary>
        /// Saves player data at set intervals.
        /// </summary>
        public void Update()
        {
            if (_timing.RealTime < _lastSave + _saveInterval)
                return;
            Save();
        }

        /// <summary>
        /// Saves player balances to the database before allowing the server to shutdown.
        /// </summary>
        public void Shutdown()
        {
            Save();
            _task.BlockWaitOnTask(Task.WhenAll(_pendingSaveTasks));
        }

        /// <summary>
        /// Loads player's balance when they connect
        /// </summary>
        public async Task PlayerConnected(ICommonSession session, CancellationToken cancel)
        {
            var user = session.UserId;
            var data = new BalanceData();
            _balances.Add(user, data);

            data.Balance = await GetBalanceAsync(user);
            cancel.ThrowIfCancellationRequested();

            data.ApplyDelta();
            data.Initialized = true;
        }

        /// <summary>
        /// Saves player's balance when they disconnect
        /// </summary>
        public void PlayerDisconnected(ICommonSession session)
        {
            SavePlayer(session.UserId);
            _balances.Remove(session.UserId);
        }

        /// <summary>
        ///  Saves player balances to the database.
        /// </summary>
        public async void Save()
        {
            foreach (var player in _balances)
                SavePlayer(player.Key);

            _lastSave = _timing.RealTime;
        }

        /// <summary>
        /// Save balance for a player to the database.
        /// </summary>
        public async void SavePlayer(NetUserId player)
        {
            if (!_balances[player].IsDirty)
                return;
            _balances[player].IsDirty = false;
            TrackPending(ModifyBalanceAsync(player, _balances[player].BalanceDelta));
            _balances[player].ApplyDelta();
        }

        /// <summary>
        /// Checks if a player has enough currency to purchase something.
        /// </summary>
        /// <param name="userId">The player's NetUserId</param>
        /// <param name="amount">The amount of currency needed.</param>
        /// <param name="balance">The player's balance.</param>
        /// <returns>Returns true if the player has enough in their balance.</returns>
        public bool CanAfford(NetUserId userId, int amount, out int balance)
        {
            balance = GetBalance(userId);
            return balance >= amount && balance - amount >= 0;
        }

        /// <summary>
        /// Converts an integer to a string representing the count followed by the appropriate currency localization (singular or plural) defined in server_currency.ftl.
        /// Useful for displaying balances and prices.
        /// </summary>
        /// <param name="amount">The amount of currency to display.</param>
        /// <returns>A string containing the count and the correct form of the currency name.</returns>
        public string Stringify(int amount) => amount == 1
            ? $"{amount} {Loc.GetString("server-currency-name-singular")}"
            : $"{amount} {Loc.GetString("server-currency-name-plural")}";

        /// <summary>
        /// Adds currency to a player.
        /// </summary>
        /// <param name="userId">The player's NetUserId</param>
        /// <param name="amount">The amount of currency to add.</param>
        /// <returns>An integer containing the new amount of currency attributed to the player.</returns>
        public int AddCurrency(NetUserId userId, int amount) => ModifyBalance(userId, amount);

        /// <summary>
        /// Removes currency from a player.
        /// </summary>
        /// <param name="userId">The player's NetUserId</param>
        /// <param name="amount">The amount of currency to remove.</param>
        /// <returns>An integer containing the new amount of currency attributed to the player.</returns>
        public int RemoveCurrency(NetUserId userId, int amount) => ModifyBalance(userId, -amount);

        /// <summary>
        /// Sets a player's currency.
        /// </summary>
        /// <param name="userId">The player's NetUserId</param>
        /// <param name="amount">The amount of currency that will be set.</param>
        /// <returns>An integer containing the new amount of currency attributed to the player.</returns>
        public int SetBalance(NetUserId userId, int amount)
        {
            if (!_balances.TryGetValue(userId, out var data) || !data.Initialized)
            {
                _sawmill.Warning($"Attempted to set balance, which was not loaded for player {userId.ToString()}");
                return 0;
            }

            var balanceData = _balances[userId];

            if (_player.TryGetSessionById(userId, out var userSession))
                BalanceChange?.Invoke(new PlayerBalanceChangeEvent(userSession, userId, amount, balanceData.Balance));

            balanceData.Balance = amount;
            balanceData.IsDirty = true;
            return amount;
        }

        /// <summary>
        /// Gets a player's balance.
        /// </summary>
        /// <param name="userId">The player's NetUserId</param>
        /// <returns>The players balance.</returns>
        public int GetBalance(NetUserId userId)
        {
            if (!_balances.TryGetValue(userId, out var data) || !data.Initialized)
            {
                _sawmill.Warning($"Attempted to get balance, which was not loaded for player {userId.ToString()}");
                return 0;
            }

            return data.Balance;
        }

        /// <summary>
        /// Modifies a player's balance by an amount.
        /// </summary>
        /// <param name="userId">The player's NetUserId</param>
        /// <param name="amountDelta">The amount of currency the player will gain/lose</param>
        /// <returns>The players balance.</returns>
        public int ModifyBalance(NetUserId userId, int amountDelta)
        {
            if (!_balances.TryGetValue(userId, out var data) || !data.Initialized)
            {
                _sawmill.Warning($"Attempted to modify balance, which was not loaded for player {userId.ToString()}");
                return 0;
            }

            var balanceData = _balances[userId];

            if (_player.TryGetSessionById(userId, out var userSession))
                BalanceChange?.Invoke(new PlayerBalanceChangeEvent(userSession, userId, balanceData.Balance + amountDelta, balanceData.Balance));

            balanceData.Balance += amountDelta;
            balanceData.IsDirty = true;
            return balanceData.Balance;
        }

        /// <summary>
        /// Balance info for a particular player.
        /// </summary>
        /// <remarks>
        /// Edits to <see cref="Balance"/> are stored in <see cref="BalanceDelta"/> and are accessible until <see cref="ApplyDelta"/> is called
        /// </remarks>
        private sealed class BalanceData
        {
            private int _balance = new();
            private int _balanceDelta = new();
            public bool IsDirty = false;
            public bool Initialized = false;

            public int Balance { get => _balance + _balanceDelta; set => _balanceDelta = value - _balance; }
            public int BalanceDelta => _balanceDelta;
            public void ApplyDelta() { _balance += _balanceDelta; _balanceDelta = 0; }
        }

        // Async Tasks

        /// <summary>
        /// Sets a player's balance.
        /// </summary>
        /// <param name="userId">The player's NetUserId</param>
        /// <param name="amount">The amount of currency that will be set.</param>
        /// <returns>An integer containing the new amount of currency attributed to the player.</returns>
        public async Task SetBalanceAsync(NetUserId userId, int amount) => await _db.SetServerCurrency(userId, amount);

        /// <summary>
        /// Gets a player's balance.
        /// </summary>
        /// <param name="userId">The player's NetUserId</param>
        /// <returns>An integer containing the amount of currency attributed to the player.</returns>
        public async Task<int> GetBalanceAsync(NetUserId userId) => await _db.GetServerCurrency(userId);

        /// <summary>
        /// Changes player's balance by an amount.
        /// </summary>
        /// <param name="userId">The player's NetUserId</param>
        /// <param name="amountDelta">Change in currency amount</param>
        public async Task ModifyBalanceAsync(NetUserId userId, int amountDelta) => await _db.ModifyServerCurrency(userId, amountDelta);

        /// <summary>
        /// Track a database save task to make sure we block server shutdown on it.
        /// </summary>
        private async void TrackPending(Task task)
        {
            _pendingSaveTasks.Add(task);

            try
            {
                await task;
            }
            finally
            {
                _pendingSaveTasks.Remove(task);
            }
        }

    }
}
