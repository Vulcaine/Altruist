import { ComponentFixture, TestBed } from '@angular/core/testing';

import { SceneViewComponent } from './scene-view.component';

describe('SceneViewComponent', () => {
  let component: SceneViewComponent;
  let fixture: ComponentFixture<SceneViewComponent>;

  beforeEach(async () => {
    await TestBed.configureTestingModule({
      imports: [SceneViewComponent]
    })
    .compileComponents();

    fixture = TestBed.createComponent(SceneViewComponent);
    component = fixture.componentInstance;
    fixture.detectChanges();
  });

  it('should create', () => {
    expect(component).toBeTruthy();
  });
});
