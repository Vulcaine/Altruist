import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  WorldPartitionDto,
  WorldSummary,
} from '../world-scene/models/world.model';

export interface WorldSnapshotDto {
  partitions: WorldPartitionDto[];
}

@Injectable({ providedIn: 'root' })
export class WorldDashboardService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/dashboard/v1/worlds';

  getWorlds(): Observable<WorldSummary[]> {
    return this.http.get<WorldSummary[]>(this.baseUrl);
  }

  /**
   * ✅ Snapshot (non-stream) world objects.
   * Used for seamless refresh + auto-update polling.
   *
   * Backend endpoint expected:
   *   GET /dashboard/v1/worlds/{worldIndex}/objects
   *
   * Response:
   *   { partitions: WorldPartitionDto[] }
   */
  getWorldObjectsSnapshot(worldIndex: number): Observable<WorldSnapshotDto> {
    return this.http.get<WorldSnapshotDto>(
      `${this.baseUrl}/${worldIndex}/objects`,
    );
  }

  /**
   * NDJSON stream of partitions.
   * Useful for large worlds / incremental load.
   *
   * Backend endpoint:
   *   GET /dashboard/v1/worlds/{worldIndex}/objects/stream
   */
  streamWorldObjects(worldIndex: number): Observable<WorldPartitionDto> {
    return new Observable<WorldPartitionDto>((observer) => {
      const xhr = new XMLHttpRequest();
      xhr.open('GET', `${this.baseUrl}/${worldIndex}/objects/stream`);
      xhr.responseType = 'text';

      let lastIndex = 0;
      let buffer = '';

      xhr.onprogress = () => {
        const text = xhr.responseText;

        buffer += text.substring(lastIndex);
        lastIndex = text.length;

        const lines = buffer.split('\n');
        buffer = lines.pop() ?? '';

        for (const line of lines) {
          const trimmed = line.trim();
          if (!trimmed) continue;

          try {
            const parsed = JSON.parse(trimmed) as WorldPartitionDto;
            observer.next(parsed);
          } catch (err) {
            console.error('Failed to parse NDJSON line:', trimmed, err);
          }
        }
      };

      xhr.onload = () => {
        const final = buffer.trim();
        if (final) {
          try {
            observer.next(JSON.parse(final) as WorldPartitionDto);
          } catch (err) {
            console.error('Failed to parse final NDJSON line:', final, err);
          }
        }
        observer.complete();
      };

      xhr.onerror = () =>
        observer.error(xhr.statusText || 'NDJSON stream error');

      xhr.send();

      return () => {
        xhr.abort();
      };
    });
  }
}
