import * as signalR from "@microsoft/signalr";

export const TRANSACTION_RECEIVED_EVENT = "TransactionReceived";

export function createHubConnection(): signalR.HubConnection {
  return new signalR.HubConnectionBuilder()
    .withUrl("/hubs/transactions")
    .withAutomaticReconnect()
    .configureLogging(signalR.LogLevel.Warning)
    .build();
}
