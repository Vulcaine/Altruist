export interface DashboardVector3 {
  x: number;
  y: number;
  z: number;
}

export interface DashboardWorldObjectStateDto {
  id: string;
  archetype: string;
  position: DashboardVector3;
}

export interface DashboardWorldObjectStatePacket {
  worldIndex: number;
  timestampUtc: string;
  objects: DashboardWorldObjectStateDto[];
}
