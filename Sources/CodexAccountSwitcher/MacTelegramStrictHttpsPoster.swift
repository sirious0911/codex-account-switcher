import Foundation

/// SOURCE ONLY: macOS Telegram HTTPS one-shot transport. The production
/// AppModel does NOT construct or call it. No Keychain or Telegram action
/// occurs until an independently approved caller explicitly calls postOnce.
///
/// This prevents app-level retries and HTTP redirects. URLSession/CFNetwork
/// may still perform lower-level connection recovery, so a strictly proven
/// physical single network attempt requires native review before live use.
@MainActor
final class MacTelegramStrictHttpsPoster {
    typealias Transport = @Sendable (URLRequest) async throws -> (Data, URLResponse)

    enum PosterError: Error {
        case invalidRequest
        case unexpectedResponse
    }

    private let syntheticTransport: Transport?
    private let noRedirect = MacTelegramStrictNoRedirectDelegate()
    private let session: URLSession

    init(syntheticTransport: Transport? = nil) {
        self.syntheticTransport = syntheticTransport
        let configuration = URLSessionConfiguration.ephemeral
        configuration.requestCachePolicy = .reloadIgnoringLocalCacheData
        configuration.urlCache = nil
        configuration.httpCookieStorage = nil
        configuration.httpShouldSetCookies = false
        configuration.httpCookieAcceptPolicy = .never
        configuration.waitsForConnectivity = false
        configuration.isDiscretionary = false
        configuration.httpMaximumConnectionsPerHost = 1
        configuration.tlsMinimumSupportedProtocolVersion = .TLSv12
        session = URLSession(
            configuration: configuration,
            delegate: noRedirect,
            delegateQueue: nil)
    }

    func dispose() {
        session.invalidateAndCancel()
    }

    /// Validates a request from MacTelegramBotApiContract and invokes one
    /// synthetic or native POST exactly once. Never logs a request URL
    /// containing the bot token, body, chat ID or response.
    func postOnce(_ request: URLRequest) async throws -> (Data, Int) {
        guard Self.validRequest(request), !Task.isCancelled
        else { throw PosterError.invalidRequest }

        let (data, response): (Data, URLResponse)
        if let syntheticTransport {
            (data, response) = try await syntheticTransport(request)
        } else {
            (data, response) = try await session.data(for: request)
        }
        guard !Task.isCancelled,
              let http = response as? HTTPURLResponse,
              http.url?.absoluteString == request.url?.absoluteString,
              data.count <= 32_768,
              http.statusCode == 200
        else { throw PosterError.unexpectedResponse }
        return (data, http.statusCode)
    }

    private static func validRequest(_ request: URLRequest) -> Bool {
        guard request.httpMethod == "POST",
              let url = request.url,
              url.scheme == "https", url.host == "api.telegram.org",
              url.port == nil, url.user == nil, url.password == nil,
              url.query == nil, url.fragment == nil,
              url.path.hasPrefix("/bot"), url.path.hasSuffix("/sendMessage"),
              let body = request.httpBody, !body.isEmpty,
              body.count <= 32_768,
              request.value(forHTTPHeaderField: "Content-Type") ==
                  "application/json",
              request.timeoutInterval > 0, request.timeoutInterval <= 15
        else { return false }

        // Avoid accepting arbitrary cross-origin URLs or URL-shaped token
        // strings from external callbacks. No attempt is made to log tokens.
        let token = String(url.path.dropFirst(4).dropLast("/sendMessage".count))
        let parts = token.split(separator: ":", omittingEmptySubsequences: false)
        guard parts.count == 2,
              (4...20).contains(parts[0].utf8.count),
              (24...128).contains(parts[1].utf8.count),
              parts[0].utf8.allSatisfy({ (48...57).contains($0) }),
              parts[1].utf8.allSatisfy({
                  (48...57).contains($0) || (65...90).contains($0) ||
                  (97...122).contains($0) || $0 == 45 || $0 == 95
              }),
              let json = try? JSONSerialization.jsonObject(with: body)
                    as? [String: Any],
              Set(json.keys) == Set([
                  "chat_id", "text", "disable_notification",
                  "link_preview_options"
              ]),
              let id = json["chat_id"] as? Int64, id != 0,
              let text = json["text"] as? String,
              !text.isEmpty, text.count <= 4096,
              json["disable_notification"] is Bool,
              let preview = json["link_preview_options"] as? [String: Any],
              preview.count == 1,
              preview["is_disabled"] as? Bool == true
        else { return false }
        return true
    }
}

/// Disallows all redirect hops (even back to Telegram) and declines
/// non-TLS authentication challenges; default system trust is still used.
private final class MacTelegramStrictNoRedirectDelegate: NSObject,
    URLSessionTaskDelegate, @unchecked Sendable {
    func urlSession(
        _ session: URLSession, task: URLSessionTask,
        willPerformHTTPRedirection response: HTTPURLResponse,
        newRequest request: URLRequest,
        completionHandler: @escaping (URLRequest?) -> Void
    ) {
        completionHandler(nil)
    }

    func urlSession(
        _ session: URLSession, task: URLSessionTask,
        didReceive challenge: URLAuthenticationChallenge,
        completionHandler: @escaping (
            URLSession.AuthChallengeDisposition, URLCredential?) -> Void
    ) {
        if challenge.protectionSpace.authenticationMethod ==
            NSURLAuthenticationMethodServerTrust {
            completionHandler(.performDefaultHandling, nil)
        } else {
            completionHandler(.cancelAuthenticationChallenge, nil)
        }
    }
}
