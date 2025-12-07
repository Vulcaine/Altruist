export interface WorldSummary {
  index: number;
  name: string;
  partitionCount: number;
  objectCount: number;
}

export interface Vector3Dto {
  x: number;
  y: number;
  z: number;
}

export interface TransformDto {
  position: Vector3Dto;
  size: Vector3Dto;
  scale: Vector3Dto;
}

export interface WorldObjectDto {
  instanceId: string;
  archetype: string;
  zoneId: string;
  clientId: string;
  expired: boolean;
  transform: TransformDto;
}
