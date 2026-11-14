import { renderTelegramMock, renderTaskbarMock } from "./offline-core.mjs";

/**
 * Format-only destinations representing the existing two Telegram bot roles.
 * No bot token, chat ID, HTTP client, message queue or network request is used.
 */
export function renderMonitorBotPreview(observation) {
  return {
    target: "MOCK_MONITOR_BOT",
    text: "[모의 모니터] " + renderTaskbarMock(observation)
  };
}

export function renderAlertBotPreview(event) {
  return {
    target: "MOCK_ALERT_BOT",
    text: renderTelegramMock(event)
  };
}
