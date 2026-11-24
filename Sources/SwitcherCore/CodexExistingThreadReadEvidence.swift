import Foundation

#if os(macOS)
/// Fixed four-read sequence in one initialized session. The output is
/// evidence for review only, not permission to send or proof of process exit.
public struct CodexExistingThreadReadEvidence: Sendable {
    public let accountBefore: Data
    public let catalog: Data
    public let thread: Data
    public let accountAfter: Data

    enum Step: Int, CaseIterable, Sendable {
        case accountBefore = 1, catalog, thread, accountAfter
        var id: Int { rawValue }
        var method: String {
            switch self {
            case .accountBefore, .accountAfter: "account/read"
            case .catalog: "thread/list"
            case .thread: "thread/read"
            }
        }
        func parameters(selectedID: String) -> [String: Any] {
            switch self {
            case .accountBefore, .accountAfter: ["refreshToken": false]
            case .catalog: ["currentAccountOnly": true,
                            "searchTerm": "Codex 사용량 초기화 전용", "limit": 100]
            case .thread: ["threadId": selectedID, "includeTurns": false]
            }
        }
    }

    static func validID(_ value: String) -> Bool {
        guard let id = UUID(uuidString: value) else { return false }
        return id.uuidString.lowercased() == value.lowercased()
    }

    private static func json(_ value: [String: Any]) throws -> Data {
        let data = try JSONSerialization.data(withJSONObject: value, options: [.sortedKeys])
        guard data.count <= 1_048_576 else { throw CodexClientError.malformedResponse }
        return data
    }

    private static func account(_ value: JSONValue) throws -> Data {
        guard let account = value["account"], account["type"]?.stringValue == "chatgpt",
              let email = account["email"]?.stringValue, email.utf8.count <= 254
        else { throw CodexClientError.identityUnavailable }
        return try json(["account": ["type":"chatgpt", "email":email]])
    }

    private static func list(_ value: JSONValue) throws -> Data {
        guard case let .array(entries)? = value["data"], entries.count <= 100
        else { throw CodexClientError.malformedResponse }
        var projected: [[String: String]] = []
        for item in entries {
            guard let id = item["id"]?.stringValue, validID(id),
                  let title = item["name"]?.stringValue ?? item["title"]?.stringValue,
                  title.utf8.count <= 4096 else { throw CodexClientError.malformedResponse }
            projected.append(["id":id,"name":title])
        }
        if let cursor = value["nextCursor"], case .null = cursor {} else if value["nextCursor"] != nil {
            throw CodexClientError.malformedResponse
        }
        return try json(["data": projected])
    }

    private static func detail(_ value: JSONValue, selectedID: String) throws -> Data {
        guard let item = value["thread"], item["id"]?.stringValue == selectedID,
              item["ephemeral"]?.boolValue == false,
              let path = item["path"]?.stringValue, path.hasPrefix("/"),
              path.utf8.count <= 4096, !path.contains("\0"),
              let status = item["status"]?["type"]?.stringValue ?? item["status"]?.stringValue,
              ["idle","notLoaded"].contains(status)
        else { throw CodexClientError.malformedResponse }
        var result: [String: Any] = ["id":selectedID,"ephemeral":false,"path":path,
                                     "status":["type":status]]
        if let name = item["name"]?.stringValue { result["name"] = name }
        return try json(["thread":result])
    }

    static func collect(selectedID: String,
                        read: (Step) async throws -> JSONValue) async throws -> Self {
        guard validID(selectedID) else { throw CodexClientError.malformedResponse }
        let before = try account(await read(.accountBefore))
        try Task.checkCancellation()
        let catalog = try list(await read(.catalog))
        try Task.checkCancellation()
        let thread = try detail(await read(.thread), selectedID:selectedID)
        try Task.checkCancellation()
        let after = try account(await read(.accountAfter))
        try Task.checkCancellation()
        return Self(accountBefore:before,catalog:catalog,thread:thread,accountAfter:after)
    }
}
#endif
