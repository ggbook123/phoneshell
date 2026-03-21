class DeviceInfo {
  final String deviceId;
  String displayName;
  final String os;
  final bool isOnline;
  final List<String> availableShells;

  DeviceInfo({
    required this.deviceId,
    required this.displayName,
    required this.os,
    required this.isOnline,
    required this.availableShells,
  });

  factory DeviceInfo.fromMap(Map<String, dynamic> map) {
    return DeviceInfo(
      deviceId: (map['deviceId'] ?? '') as String,
      displayName: (map['displayName'] ?? map['deviceId'] ?? '') as String,
      os: (map['os'] ?? 'Unknown') as String,
      isOnline: (map['isOnline'] ?? true) as bool,
      availableShells: (map['availableShells'] is List)
          ? (map['availableShells'] as List).map((e) => e.toString()).toList()
          : <String>[],
    );
  }

  DeviceInfo copyWith({
    String? displayName,
    bool? isOnline,
    List<String>? availableShells,
  }) {
    return DeviceInfo(
      deviceId: deviceId,
      displayName: displayName ?? this.displayName,
      os: os,
      isOnline: isOnline ?? this.isOnline,
      availableShells: availableShells ?? this.availableShells,
    );
  }
}

class GroupMemberInfo {
  final String deviceId;
  String displayName;
  final String os;
  final String role;
  final bool isOnline;
  final List<String> availableShells;

  GroupMemberInfo({
    required this.deviceId,
    required this.displayName,
    required this.os,
    required this.role,
    required this.isOnline,
    required this.availableShells,
  });

  factory GroupMemberInfo.fromMap(Map<String, dynamic> map) {
    return GroupMemberInfo(
      deviceId: (map['deviceId'] ?? '') as String,
      displayName: (map['displayName'] ?? '') as String,
      os: (map['os'] ?? '') as String,
      role: (map['role'] ?? 'Member') as String,
      isOnline: (map['isOnline'] ?? true) as bool,
      availableShells: (map['availableShells'] is List)
          ? (map['availableShells'] as List).map((e) => e.toString()).toList()
          : <String>[],
    );
  }
}

class SingleDeviceRecord {
  String deviceId = '';
  String displayName = '';
  String wsUrl = '';
  String httpUrl = '';

  Map<String, dynamic> toJson() {
    return {
      'deviceId': deviceId,
      'displayName': displayName,
      'wsUrl': wsUrl,
      'httpUrl': httpUrl,
    };
  }

  static SingleDeviceRecord fromJson(Map<String, dynamic> json) {
    final record = SingleDeviceRecord();
    record.deviceId = (json['deviceId'] ?? '') as String;
    record.displayName = (json['displayName'] ?? '') as String;
    record.wsUrl = (json['wsUrl'] ?? '') as String;
    record.httpUrl = (json['httpUrl'] ?? '') as String;
    return record;
  }
}

class SessionState {
  String sessionId = '';
  String deviceId = '';
  String bufferedOutput = '';
  String shellId = '';
}

class SessionItem {
  String sessionId = '';
  String shellId = '';
  String title = '';
}
