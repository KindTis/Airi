# 창 크롬과 작업 버튼 스타일 통일 구현 계획

> **에이전트 작업자:** 이 계획을 작업별로 구현할 때 `superpowers:subagent-driven-development`(권장) 또는 `superpowers:executing-plans` 하위 스킬을 반드시 사용한다. 진행 추적은 체크박스(`- [ ]`)를 사용한다.

**목표:** 메인 창의 타이틀바를 앱 팔레트에 맞추고, 두 편집 다이얼로그를 borderless로 만들며, 직접 배치된 작업 버튼을 주요·보조 색상 역할을 유지한 하나의 둥근 형태로 통일한다.

**아키텍처:** 기존 `CustomWindowStyle`을 메인 창에 재사용하고 두 다이얼로그는 WPF `WindowStyle.None`을 사용한다. 앱 리소스에 하나의 공통 버튼 템플릿과 이를 상속하는 주요·보조 스타일 두 개만 추가하고, 대상 버튼에 명시적으로 적용해 내부 컨트롤에는 영향을 주지 않는다.

**기술 스택:** .NET 9, WPF XAML, xUnit, 기존 `WpfTestHost`

## 전역 제약

- 새 NuGet 패키지, 코드 비하인드, ViewModel, 서비스 또는 도메인 변경을 추가하지 않는다.
- 검색 지우기 버튼, 타이틀바 버튼과 날짜 선택기 내부 버튼은 변경하지 않는다.
- 기존 창 크기·배치·문구·명령·클릭 처리와 버튼별 너비·높이·여백을 유지한다.
- 주요 버튼은 `#3B82F6`, 보조 버튼은 `#1E2234`를 기본 배경으로 사용한다.
- 대상 작업 버튼은 14px 둥근 모서리와 1px 테두리를 사용한다.
- 사용자 대상 문서는 한국어 UTF-8(BOM 없음)으로 유지한다.

---

### 작업 1: 창 크롬 통일

**파일:**

- 생성: `tests/Airi.Tests/WindowVisualStyleTests.cs`
- 수정: `MainWindow.xaml:10-12`
- 수정: `Views/MetadataEditorWindow.xaml:5-8`
- 수정: `Views/ThumbnailSelectionWindow.xaml:4-8`

**인터페이스:**

- 사용: 앱 리소스 키 `CustomWindowStyle`, WPF `WindowStyle.None`
- 생성: 새 공개 C# API 없음

- [ ] **1단계: 현재 창 크롬에서 실패하는 WPF 테스트 작성**

`tests/Airi.Tests/WindowVisualStyleTests.cs`를 다음 내용으로 생성한다.

```csharp
using System.IO;
using System.Windows;
using System.Windows.Media;
using Airi.Services;
using Airi.Views;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class WindowVisualStyleTests
{
    [Fact]
    public Task Windows_UseRequestedChrome() => WpfTestHost.RunAsync(() =>
    {
        var mainWindow = new MainWindow();
        var metadataWindow = new MetadataEditorWindow(CreateVideoItem());
        var thumbnailWindow = new ThumbnailSelectionWindow(CreateCandidates());

        try
        {
            var application = Application.Current
                ?? throw new InvalidOperationException("WPF application is not initialized.");
            var customWindowStyle = Assert.IsType<Style>(
                application.TryFindResource("CustomWindowStyle"));

            Assert.Same(customWindowStyle, mainWindow.Style);
            var titleBarBrush = Assert.IsType<SolidColorBrush>(mainWindow.BorderBrush);
            Assert.Equal(Color.FromRgb(0x15, 0x18, 0x28), titleBarBrush.Color);
            Assert.Equal(WindowStyle.None, metadataWindow.WindowStyle);
            Assert.Equal(WindowStyle.None, thumbnailWindow.WindowStyle);
        }
        finally
        {
            thumbnailWindow.Close();
            metadataWindow.Close();
            mainWindow.Close();
        }

        return Task.CompletedTask;
    });

    private static VideoItem CreateVideoItem() => new()
    {
        Title = "Sample",
        Description = string.Empty,
        Actors = [],
        Tags = []
    };

    private static VideoThumbnailCandidate[] CreateCandidates()
    {
        var imagePath = Path.Combine(AppContext.BaseDirectory, "resources", "noimage.jpg");
        return Enumerable.Range(1, 5)
            .Select(index => new VideoThumbnailCandidate(TimeSpan.FromSeconds(index), imagePath))
            .ToArray();
    }
}
```

- [ ] **2단계: 테스트가 요청한 차이 때문에 실패하는지 확인**

실행:

```powershell
dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug --filter "FullyQualifiedName~WindowVisualStyleTests.Windows_UseRequestedChrome"
```

예상: `mainWindow.Style`이 `CustomWindowStyle`이 아니어서 테스트 1개가 실패한다. 컴파일 오류나 테스트 호스트 오류가 나오면 테스트부터 수정해 같은 assertion failure를 확인한다.

- [ ] **3단계: 메인 창에 기존 커스텀 타이틀바 적용**

`MainWindow.xaml` 루트 `Window`의 끝부분을 다음과 같이 바꾼다.

```xml
        mc:Ignorable="d"
        Title="Airi" Height="1000" Width="1500"
        Style="{DynamicResource CustomWindowStyle}"
        Background="{DynamicResource ShellBackgroundBrush}"
        BorderBrush="#151828">
```

`CustomWindowStyle`은 타이틀바 배경에 창의 `BorderBrush`를 사용하므로 별도 타이틀바 구현을 추가하지 않는다.

- [ ] **4단계: 두 다이얼로그에서 네이티브 프레임 제거**

`Views/MetadataEditorWindow.xaml`의 루트 설정을 다음과 같이 바꾼다.

```xml
        Title="메타데이터 편집" Width="720" MinHeight="860"
        WindowStartupLocation="CenterOwner"
        ResizeMode="NoResize" Background="#0F111C" Foreground="#E8ECF9"
        WindowStyle="None" ShowInTaskbar="False">
```

`Views/ThumbnailSelectionWindow.xaml`의 루트 설정을 다음과 같이 바꾼다.

```xml
        Title="썸네일 선택" Width="1120" Height="480"
        WindowStartupLocation="CenterOwner" ResizeMode="NoResize"
        Background="#0F111C" Foreground="#E8ECF9"
        WindowStyle="None" ShowInTaskbar="False"
        PreviewKeyDown="OnWindowPreviewKeyDown">
```

- [ ] **5단계: 창 크롬 테스트가 통과하는지 확인**

실행:

```powershell
dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug --filter "FullyQualifiedName~WindowVisualStyleTests.Windows_UseRequestedChrome"
```

예상: 테스트 1개 통과, 실패 0개.

- [ ] **6단계: 창 크롬 변경 커밋**

```powershell
git add MainWindow.xaml Views/MetadataEditorWindow.xaml Views/ThumbnailSelectionWindow.xaml tests/Airi.Tests/WindowVisualStyleTests.cs
git commit -m "style: align window chrome with app theme"
```

---

### 작업 2: 작업 버튼 형태와 역할 색상 통일

**파일:**

- 수정: `tests/Airi.Tests/WindowVisualStyleTests.cs`
- 수정: `App.xaml:5-17`
- 수정: `MainWindow.xaml:352-356,584-592`
- 수정: `Views/MetadataEditorWindow.xaml:117-166,199-204,271-280`
- 수정: `Views/ThumbnailSelectionWindow.xaml:74-82`

**인터페이스:**

- 사용: 작업 1에서 변경한 세 창의 XAML
- 생성: 앱 리소스 키 `ActionButtonBaseStyle`, `PrimaryActionButtonStyle`, `SecondaryActionButtonStyle`
- 생성: 새 공개 C# API 없음

- [ ] **1단계: 버튼 역할과 공통 형태에서 실패하는 테스트 추가**

`tests/Airi.Tests/WindowVisualStyleTests.cs` 전체를 다음 내용으로 바꾼다.

```csharp
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Airi.Services;
using Airi.Views;

namespace Airi.Tests;

[Collection(WpfTestCollection.Name)]
public sealed class WindowVisualStyleTests
{
    [Fact]
    public Task Windows_UseRequestedChrome() => WpfTestHost.RunAsync(() =>
    {
        var mainWindow = new MainWindow();
        var metadataWindow = new MetadataEditorWindow(CreateVideoItem());
        var thumbnailWindow = new ThumbnailSelectionWindow(CreateCandidates());

        try
        {
            var application = Application.Current
                ?? throw new InvalidOperationException("WPF application is not initialized.");
            var customWindowStyle = Assert.IsType<Style>(
                application.TryFindResource("CustomWindowStyle"));

            Assert.Same(customWindowStyle, mainWindow.Style);
            var titleBarBrush = Assert.IsType<SolidColorBrush>(mainWindow.BorderBrush);
            Assert.Equal(Color.FromRgb(0x15, 0x18, 0x28), titleBarBrush.Color);
            Assert.Equal(WindowStyle.None, metadataWindow.WindowStyle);
            Assert.Equal(WindowStyle.None, thumbnailWindow.WindowStyle);
        }
        finally
        {
            thumbnailWindow.Close();
            metadataWindow.Close();
            mainWindow.Close();
        }

        return Task.CompletedTask;
    });

    [Fact]
    public Task ActionButtons_UseRoleStylesAndSharedShape() => WpfTestHost.RunAsync(() =>
    {
        var application = Application.Current
            ?? throw new InvalidOperationException("WPF application is not initialized.");
        var primaryStyle = Assert.IsType<Style>(
            application.TryFindResource("PrimaryActionButtonStyle"));
        var secondaryStyle = Assert.IsType<Style>(
            application.TryFindResource("SecondaryActionButtonStyle"));
        var mainWindow = new MainWindow();
        var metadataWindow = new MetadataEditorWindow(CreateVideoItem());
        var thumbnailWindow = new ThumbnailSelectionWindow(CreateCandidates());

        try
        {
            AssertBrushSetter(
                primaryStyle,
                Control.BackgroundProperty,
                Color.FromRgb(0x3B, 0x82, 0xF6));
            AssertBrushSetter(
                secondaryStyle,
                Control.BackgroundProperty,
                Color.FromRgb(0x1E, 0x22, 0x34));
            AssertButtonsUseStyle(mainWindow, primaryStyle, "Random Play", "Fetch Metadata");
            AssertButtonsUseStyle(
                metadataWindow,
                primaryStyle,
                "파일 선택",
                "썸네일 생성",
                "저장");
            AssertButtonsUseStyle(
                metadataWindow,
                secondaryStyle,
                "초기화",
                "Try Parse On 141Jav",
                "취소");
            AssertButtonsUseStyle(thumbnailWindow, primaryStyle, "다시 생성", "선택");
            AssertButtonsUseStyle(thumbnailWindow, secondaryStyle, "취소");

            var clearSearchButton = FindDescendants<Button>(mainWindow)
                .Single(button => ReferenceEquals(button.Command, mainWindow.ViewModel.ClearSearchCommand));
            Assert.NotSame(primaryStyle, clearSearchButton.Style);
            Assert.NotSame(secondaryStyle, clearSearchButton.Style);

            AssertRounded(FindButton(mainWindow, "Random Play"));
            AssertRounded(FindButton(metadataWindow, "취소"));
        }
        finally
        {
            thumbnailWindow.Close();
            metadataWindow.Close();
            mainWindow.Close();
        }

        return Task.CompletedTask;
    });

    private static void AssertBrushSetter(
        Style style,
        DependencyProperty property,
        Color expectedColor)
    {
        var setter = Assert.Single(
            style.Setters.OfType<Setter>(),
            candidate => candidate.Property == property);
        var brush = Assert.IsType<SolidColorBrush>(setter.Value);
        Assert.Equal(expectedColor, brush.Color);
    }

    private static void AssertButtonsUseStyle(
        DependencyObject root,
        Style expectedStyle,
        params string[] labels)
    {
        foreach (var label in labels)
        {
            Assert.Same(expectedStyle, FindButton(root, label).Style);
        }
    }

    private static void AssertRounded(Button button)
    {
        button.ApplyTemplate();
        var border = Assert.IsType<Border>(
            button.Template.FindName("ActionButtonBorder", button));
        Assert.Equal(new CornerRadius(14), border.CornerRadius);
    }

    private static Button FindButton(DependencyObject root, string label) =>
        Assert.Single(FindDescendants<Button>(root), button => Equals(button.Content, label));

    private static IEnumerable<T> FindDescendants<T>(DependencyObject root)
        where T : DependencyObject
    {
        foreach (var child in LogicalTreeHelper.GetChildren(root).OfType<DependencyObject>())
        {
            if (child is T match)
            {
                yield return match;
            }

            foreach (var descendant in FindDescendants<T>(child))
            {
                yield return descendant;
            }
        }
    }

    private static VideoItem CreateVideoItem() => new()
    {
        Title = "Sample",
        Description = string.Empty,
        Actors = [],
        Tags = []
    };

    private static VideoThumbnailCandidate[] CreateCandidates()
    {
        var imagePath = Path.Combine(AppContext.BaseDirectory, "resources", "noimage.jpg");
        return Enumerable.Range(1, 5)
            .Select(index => new VideoThumbnailCandidate(TimeSpan.FromSeconds(index), imagePath))
            .ToArray();
    }
}
```

- [ ] **2단계: 새 버튼 테스트가 스타일 리소스 부재로 실패하는지 확인**

실행:

```powershell
dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug --filter "FullyQualifiedName~WindowVisualStyleTests.ActionButtons_UseRoleStylesAndSharedShape"
```

예상: `PrimaryActionButtonStyle`이 없어 `Assert.IsType<Style>` assertion failure로 테스트 1개가 실패한다.

- [ ] **3단계: 앱 범위의 명시적 주요·보조 버튼 스타일 추가**

`App.xaml`의 `ResourceDictionary.MergedDictionaries` 닫는 태그 다음에 아래 스타일 세 개를 추가한다.

```xml
            <Style x:Key="ActionButtonBaseStyle"
                   TargetType="{x:Type Button}"
                   BasedOn="{StaticResource {x:Type Button}}">
                <Setter Property="Foreground" Value="White"/>
                <Setter Property="BorderThickness" Value="1"/>
                <Setter Property="Padding" Value="12,8"/>
                <Setter Property="FontWeight" Value="SemiBold"/>
                <Setter Property="Cursor" Value="Hand"/>
                <Setter Property="HorizontalContentAlignment" Value="Center"/>
                <Setter Property="VerticalContentAlignment" Value="Center"/>
                <Setter Property="Template">
                    <Setter.Value>
                        <ControlTemplate TargetType="{x:Type Button}">
                            <Border x:Name="ActionButtonBorder"
                                    Background="{TemplateBinding Background}"
                                    BorderBrush="{TemplateBinding BorderBrush}"
                                    BorderThickness="{TemplateBinding BorderThickness}"
                                    CornerRadius="14"
                                    Padding="{TemplateBinding Padding}"
                                    SnapsToDevicePixels="True">
                                <ContentPresenter HorizontalAlignment="{TemplateBinding HorizontalContentAlignment}"
                                                  VerticalAlignment="{TemplateBinding VerticalContentAlignment}"
                                                  RecognizesAccessKey="True"
                                                  TextElement.Foreground="{TemplateBinding Foreground}"/>
                            </Border>
                        </ControlTemplate>
                    </Setter.Value>
                </Setter>
            </Style>

            <Style x:Key="PrimaryActionButtonStyle"
                   TargetType="{x:Type Button}"
                   BasedOn="{StaticResource ActionButtonBaseStyle}">
                <Setter Property="Background" Value="#3B82F6"/>
                <Setter Property="BorderBrush" Value="#3B82F6"/>
                <Style.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter Property="Background" Value="#4D8FFF"/>
                        <Setter Property="BorderBrush" Value="#4D8FFF"/>
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                        <Setter Property="Background" Value="#2F6BD8"/>
                        <Setter Property="BorderBrush" Value="#2F6BD8"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                        <Setter Property="Background" Value="#1E2234"/>
                        <Setter Property="BorderBrush" Value="#2E3350"/>
                        <Setter Property="Foreground" Value="#5E6585"/>
                    </Trigger>
                </Style.Triggers>
            </Style>

            <Style x:Key="SecondaryActionButtonStyle"
                   TargetType="{x:Type Button}"
                   BasedOn="{StaticResource ActionButtonBaseStyle}">
                <Setter Property="Background" Value="#1E2234"/>
                <Setter Property="BorderBrush" Value="#2E3350"/>
                <Style.Triggers>
                    <Trigger Property="IsMouseOver" Value="True">
                        <Setter Property="Background" Value="#242A3D"/>
                    </Trigger>
                    <Trigger Property="IsPressed" Value="True">
                        <Setter Property="Background" Value="#2E3350"/>
                    </Trigger>
                    <Trigger Property="IsEnabled" Value="False">
                        <Setter Property="Background" Value="#1E2234"/>
                        <Setter Property="BorderBrush" Value="#2E3350"/>
                        <Setter Property="Foreground" Value="#5E6585"/>
                    </Trigger>
                </Style.Triggers>
            </Style>
```

스타일은 키가 있으므로 암시적 기본 `Button` 스타일을 바꾸지 않는다.

- [ ] **4단계: 메인 창의 두 주요 버튼에 공통 스타일 적용**

`MainWindow.xaml`의 `Random Play` 버튼을 다음 요소로 바꾼다.

```xml
                <Button Grid.Column="2" Width="140" Height="42" Margin="0,0,12,0" Padding="12"
                        Content="Random Play"
                        Style="{StaticResource PrimaryActionButtonStyle}"
                        Command="{Binding RandomPlayCommand}"/>
```

`Fetch Metadata` 버튼을 다음 요소로 바꾼다.

```xml
                <Button Grid.Column="1"
                        Content="Fetch Metadata"
                        Command="{Binding FetchMetadataCommand}"
                        Height="36"
                        MinWidth="140"
                        Margin="12,0,0,0"
                        Style="{StaticResource PrimaryActionButtonStyle}"/>
```

- [ ] **5단계: 메타데이터 편집창의 로컬 버튼 템플릿 제거 및 역할 스타일 적용**

`Views/MetadataEditorWindow.xaml`에서 `PrimaryButtonStyle`과 `GhostButtonStyle` 두 로컬 `Style` 블록 전체를 삭제한다. 버튼 요소는 다음 내용으로 바꾼다.

```xml
                                        <Button x:Name="SelectThumbnailButton" Content="파일 선택"
                                                Style="{StaticResource PrimaryActionButtonStyle}"
                                                Width="100" Click="OnSelectThumbnailClick"/>
                                        <Button x:Name="GenerateThumbnailButton" Content="썸네일 생성"
                                                Style="{StaticResource PrimaryActionButtonStyle}"
                                                Width="110" Margin="8,0,0,0" Click="OnGenerateThumbnailClick"/>
                                        <Button x:Name="ResetThumbnailButton" Content="초기화"
                                                Style="{StaticResource SecondaryActionButtonStyle}"
                                                Width="88" Margin="8,0,0,0" Click="OnResetThumbnailClick"/>
```

```xml
                        <Button x:Name="TryParseOn141JavButton" Content="Try Parse On 141Jav" Width="140" Height="24"
                                Style="{StaticResource SecondaryActionButtonStyle}" Click="OnTryParseOn141JavClick"/>
```

```xml
                        <Button x:Name="CancelButton" Content="취소" Width="116" Height="24"
                                Style="{StaticResource SecondaryActionButtonStyle}" Click="OnCancelClick"/>
                        <Button x:Name="SaveButton" Content="저장" Width="116" Height="24"
                                Style="{StaticResource PrimaryActionButtonStyle}" Margin="12,0,0,0" IsDefault="True"
                                Click="OnSaveClick"/>
```

- [ ] **6단계: 썸네일 선택창 버튼에 역할 스타일 적용**

`Views/ThumbnailSelectionWindow.xaml`의 버튼 패널을 다음 내용으로 바꾼다.

```xml
        <StackPanel Grid.Row="2" Orientation="Horizontal" HorizontalAlignment="Right" Margin="0,18,0,0">
            <Button x:Name="RegenerateButton" Content="다시 생성"
                    Width="110" Height="34"
                    Style="{StaticResource PrimaryActionButtonStyle}"
                    Click="OnRegenerateClick"/>
            <Button x:Name="CancelButton" Content="취소"
                    Width="110" Height="34" Margin="12,0,0,0"
                    Style="{StaticResource SecondaryActionButtonStyle}"
                    Click="OnCancelClick"/>
            <Button x:Name="ConfirmButton" Content="선택"
                    Width="110" Height="34" Margin="12,0,0,0"
                    Style="{StaticResource PrimaryActionButtonStyle}"
                    IsDefault="True" IsEnabled="False" Click="OnConfirmClick"/>
        </StackPanel>
```

- [ ] **7단계: 버튼 테스트와 창 크롬 테스트가 함께 통과하는지 확인**

실행:

```powershell
dotnet test tests/Airi.Tests/Airi.Tests.csproj -c Debug --filter "FullyQualifiedName~WindowVisualStyleTests"
```

예상: 테스트 2개 통과, 실패 0개.

- [ ] **8단계: 전체 품질 게이트 실행**

실행:

```powershell
./.agents/skills/post-feature-test-build-gate/scripts/run_quality_gate.ps1 -Configuration Debug
```

예상: restore, Debug build, 전체 xUnit 테스트가 모두 exit code 0으로 끝나고 `Quality gate completed successfully.`가 출력된다.

- [ ] **9단계: 지식 그래프 갱신 및 최종 diff 검사**

실행:

```powershell
graphify update .
git diff --check
git status --short
```

예상: graphify 증분 갱신이 성공하고 `git diff --check` 출력이 없다. `git status --short`에는 작업 2에서 수정한 `App.xaml`, 세 창 XAML과 테스트 파일만 표시된다. `graphify-out/`은 저장소에서 무시되므로 표시되지 않는다.

- [ ] **10단계: 버튼 스타일 변경 커밋**

```powershell
git add App.xaml MainWindow.xaml Views/MetadataEditorWindow.xaml Views/ThumbnailSelectionWindow.xaml tests/Airi.Tests/WindowVisualStyleTests.cs
git commit -m "style: unify action button appearance"
```

커밋 후 `git status --short`가 비어 있는지 확인한다.
