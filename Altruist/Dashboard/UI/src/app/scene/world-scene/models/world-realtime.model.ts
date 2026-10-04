export interface DashboardVector3 {
  x: number;
  y: number;
  z: number;
}

export interface DashboardWorldObjectStateDto {
  id: string;
  archetype: string;
  position: DashboardVector3;
  name?: string;
}

export interface DashboardPartitionStateDto {
  x: number;
  y: number;
  z: number;
  objects: DashboardWorldObjectStateDto[];
}

export interface DashboardWorldObjectStatePacket {
  worldIndex: number;
  timestampUtc: string;
  objects?: DashboardWorldObjectStateDto[];
  partitions?: DashboardPartitionStateDto[];
  removedObjectIds?: string[];
}
