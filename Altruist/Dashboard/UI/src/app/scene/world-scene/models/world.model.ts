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

export interface WorldSummary {
  index: number;
  name: string;
  partitionCount: number;
  objectCount: number;
}

export type PhysxColliderShape3D = number;

export interface HeightfieldDto {
  width: number;
  height: number;
  cellSizeX: number;
  cellSizeZ: number;
  heightScale: number;
  heights: number[][];
}

export interface ColliderDto {
  id: string;
  shape: PhysxColliderShape3D;
  isTrigger: boolean;
  transform: TransformDto;
  heightfield?: HeightfieldDto | null;
}

export interface WorldObjectDto {
  instanceId: string;
  archetype: string;
  zoneId: string;
  clientId: string;
  expired: boolean;
  transform: TransformDto;
  colliders: ColliderDto[];
}

export interface WorldPartitionDto {
  indexX: number;
  indexY: number;
  indexZ: number;
  objects: WorldObjectDto[];
}
