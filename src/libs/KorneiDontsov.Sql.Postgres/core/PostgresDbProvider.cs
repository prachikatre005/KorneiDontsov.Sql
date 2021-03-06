﻿namespace KorneiDontsov.Sql.Postgres {
	using Microsoft.Extensions.Hosting;
	using Microsoft.Extensions.Logging;
	using Npgsql;
	using System;
	using System.Data;
	using System.Net.Sockets;
	using System.Threading;
	using System.Threading.Tasks;

	class PostgresDbProvider: IDbProvider {
		PostgresDbProviderSettings settings { get; }
		IHostApplicationLifetime appLifetime { get; }
		ILogger logger { get; }
		String connectionString { get; }

		public PostgresDbProvider
			(PostgresDbProviderSettings settings,
			 IHostApplicationLifetime appLifetime,
			 ILogger<PostgresDbProvider> logger) {
			this.settings = settings;
			this.appLifetime = appLifetime;
			this.logger = logger;

			var connectionStringBuilder =
				new NpgsqlConnectionStringBuilder {
					Database = settings.database,
					Host = settings.host,
					Port = settings.port,
					Username = settings.username,
					CommandTimeout = settings.defaultQueryTimeout,
					SearchPath = settings.searchPath,
					Timeout = settings.connectionTimeout,
					Pooling = true,
					MinPoolSize = settings.minPoolSize,
					MaxPoolSize = settings.maxPoolSize,
					ConnectionIdleLifetime = settings.connectionIdleLifetime,
					ConnectionPruningInterval = settings.connectionPruningInterval
				};
			switch(settings.passwordSource) {
				case PostgresPasswordSource.Text text:
					connectionStringBuilder.Password = text.value;
					break;
				case PostgresPasswordSource.PgPassFile file:
					connectionStringBuilder.Passfile = file.path;
					break;
				default:
					throw new Exception($"Password source {settings.passwordSource.GetType()} is not expected.");
			}

			connectionString = connectionStringBuilder.ConnectionString;
		}

		async Task PingDbUntilItsReady (String connectionString) {
			try {
				while(! appLifetime.ApplicationStopped.IsCancellationRequested)
					try {
						var npgsqlConnection = new NpgsqlConnection(connectionString);
						await npgsqlConnection.OpenAsync(appLifetime.ApplicationStopped);
						break;
					}
					catch(NpgsqlException ex)
						when(ex.InnerException is TimeoutException
						     || ex.InnerException is SocketException { SocketErrorCode: SocketError.ConnectionRefused }
						     || ex.InnerException is SocketException { SocketErrorCode: SocketError.TimedOut }) {
						var log = "Database is not yet ready for use.\n{connectionString}";
						logger.LogInformation(ex, log, connectionString);

						await Task.Delay(500, appLifetime.ApplicationStopped);
					}
			}
			catch(OperationCanceledException) { }
		}

		async Task HandlePingDbTask (Task pingDbTask, CancellationToken cancellationToken) {
			var whenCompletedOrCanceled = await
				Task.WhenAny(pingDbTask, Task.Delay(Timeout.Infinite, cancellationToken));
			if(whenCompletedOrCanceled != pingDbTask)
				await whenCompletedOrCanceled;
			else if(! pingDbTask.IsCompletedSuccessfully) {
				var msg =
					"Failed to await until database is ready to accept connections. "
					+ $"Connection string: {connectionString}.";
				throw new Exception(msg, pingDbTask.Exception);
			}
		}

		Task? pingDbTask = null;

		Task WhenDbIsReady (CancellationToken cancellationToken) =>
			! settings.awaitUntilDbIsReady
				? Task.CompletedTask
				: HandlePingDbTask(pingDbTask ??= PingDbUntilItsReady(connectionString), cancellationToken);

		/// <inheritdoc />
		public String databaseName => settings.database;

		/// <inheritdoc />
		public String username => settings.username;

		/// <inheritdoc />
		public Int32 defaultQueryTimeout => settings.defaultQueryTimeout;

		/// <inheritdoc />
		public async ValueTask<IManagedSqlTransaction> Begin
			(SqlAccess access,
			 IsolationLevel isolationLevel,
			 CancellationToken cancellationToken = default,
			 Int32? defaultQueryTimeout = null) {
			await WhenDbIsReady(cancellationToken);

			var npgsqlConnection = new NpgsqlConnection(connectionString);
			NpgsqlTransaction npgsqlTransaction;
			try {
				await npgsqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
				npgsqlTransaction = await
					npgsqlConnection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
			}
			catch(Exception ex) {
				NpgsqlExceptions.Handle(ex);
				throw;
			}

			var timeout = defaultQueryTimeout ?? settings.defaultQueryTimeout;
			var transaction =
				new PostgresTransaction(npgsqlConnection, npgsqlTransaction, access, isolationLevel, timeout);

			await transaction.SetAccessAsync(access, cancellationToken);
			return transaction;
		}

		/// <inheritdoc />
		public async ValueTask<IManagedRwSqlTransaction> BeginRw
			(IsolationLevel isolationLevel,
			 CancellationToken cancellationToken = default,
			 Int32? defaultQueryTimeout = null) {
			await WhenDbIsReady(cancellationToken);

			var npgsqlConnection = new NpgsqlConnection(connectionString);
			NpgsqlTransaction npgsqlTransaction;
			try {
				await npgsqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
				npgsqlTransaction = await
					npgsqlConnection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
			}
			catch(Exception ex) {
				NpgsqlExceptions.Handle(ex);
				throw;
			}

			var timeout = defaultQueryTimeout ?? settings.defaultQueryTimeout;
			var transaction =
				new PostgresRwTransaction(npgsqlConnection, npgsqlTransaction, isolationLevel, timeout);

			await transaction.SetRwAsync(cancellationToken);
			return transaction;
		}

		/// <inheritdoc />
		public async ValueTask<IManagedRoSqlTransaction> BeginRo
			(IsolationLevel isolationLevel,
			 CancellationToken cancellationToken = default,
			 Int32? defaultQueryTimeout = null) {
			await WhenDbIsReady(cancellationToken);

			var npgsqlConnection = new NpgsqlConnection(connectionString);
			NpgsqlTransaction npgsqlTransaction;
			try {
				await npgsqlConnection.OpenAsync(cancellationToken).ConfigureAwait(false);
				npgsqlTransaction = await
					npgsqlConnection.BeginTransactionAsync(isolationLevel, cancellationToken).ConfigureAwait(false);
			}
			catch(Exception ex) {
				NpgsqlExceptions.Handle(ex);
				throw;
			}

			var timeout = defaultQueryTimeout ?? settings.defaultQueryTimeout;
			var transaction =
				new PostgresRoTransaction(npgsqlConnection, npgsqlTransaction, isolationLevel, timeout);

			await transaction.SetRoAsync(cancellationToken);
			return transaction;
		}
	}
}
