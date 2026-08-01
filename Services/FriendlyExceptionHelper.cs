using System;

namespace EmulatorAutoUpdater.Services;

public static class FriendlyExceptionHelper
{
    public static string FormatUserFriendlyErrorMessage(Exception ex, string actionContext)
    {
        if (ex == null)
        {
            return $"{actionContext} 중 원인을 알 수 없는 오류가 발생했습니다.";
        }

        var innermost = GetInnermostException(ex);
        var rawMsg = innermost.Message ?? ex.Message ?? string.Empty;

        if (rawMsg.Contains("App") || rawMsg.Contains("Xaml") || rawMsg.Contains("Resource") || rawMsg.Contains("Markup") || rawMsg.Contains("생성자"))
        {
            return $"{actionContext} 화면 요소를 불러오는 중 UI 리소스 연결 오류가 발생했습니다.\n\n💡 [해결 가이드]\n프로그램을 완전히 종료한 후 다시 실행해 보시기 바랍니다.";
        }

        if (rawMsg.Contains("Access") || rawMsg.Contains("Permission") || rawMsg.Contains("Unauthorized") || rawMsg.Contains("거부"))
        {
            return $"{actionContext} 진행 중 파일 접근 권한이 거부되었습니다.\n\n💡 [해결 가이드]\n해당 폴더가 다른 프로그램에서 사용 중이 아닌지 확인하거나, 프로그램을 '관리자 권한'으로 실행해 보세요.";
        }

        if (rawMsg.Contains("Path") || rawMsg.Contains("Directory") || rawMsg.Contains("File") || rawMsg.Contains("경로"))
        {
            return $"{actionContext} 진행 중 올바르지 않은 폴더 경로가 지정되었습니다.\n\n💡 [해결 가이드]\n에뮬레이터 설치 폴더 경로가 존재하는 디렉터리인지 확인해 주세요.";
        }

        if (rawMsg.Contains("Http") || rawMsg.Contains("Socket") || rawMsg.Contains("Network") || rawMsg.Contains("Web") || rawMsg.Contains("연결"))
        {
            return $"{actionContext} 진행 중 네트워크 통신 오류가 발생했습니다.\n\n💡 [해결 가이드]\n인터넷 연결 상태 및 방화벽 설정을 확인한 후 다시 시도해 주세요.";
        }

        return $"{actionContext} 진행 중 아래와 같은 예외가 발생했습니다.\n\n내용: {rawMsg}";
    }

    private static Exception GetInnermostException(Exception ex)
    {
        var current = ex;
        while (current.InnerException != null)
        {
            current = current.InnerException;
        }
        return current;
    }
}
