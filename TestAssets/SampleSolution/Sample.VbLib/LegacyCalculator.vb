Imports System
Namespace MyCompany.Services.Legacy
    Public Class LegacyCalculator
        Public Function Add(ByVal x As Integer, ByVal y As Integer) As Integer
            Return x + y
        End Function

        Public Function FormatMessage(ByVal message As String) As String
            Return $"[VB]{message.ToUpperInvariant()}"
        End Function
    End Class
End Namespace
