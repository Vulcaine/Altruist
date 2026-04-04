export interface CacheEntryDto {
  type: string;
  typeShortName: string;
  groupId: string;
  key: string;
  value: any;
  preview?: string | null;
}

export interface CacheEntryKey {
  type: string;
  groupId: string;
  key: string;
}
