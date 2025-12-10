import { HttpClient } from '@angular/common/http';
import { inject, Injectable } from '@angular/core';
import { Observable } from 'rxjs';
import {
  WorldPartitionDto,
  WorldSummary,
} from '../world-scene/models/world.model';

@Injectable({ providedIn: 'root' })
export class WorldDashboardService {
  private readonly http = inject(HttpClient);
  private readonly baseUrl = '/dashboard/v1/worlds';

  getWorlds(): Observable<WorldSummary[]> {
    return this.http.get<WorldSummary[]>(this.baseUrl);
  }

  /** NDJSON stream of WorldPartitionDto */
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
