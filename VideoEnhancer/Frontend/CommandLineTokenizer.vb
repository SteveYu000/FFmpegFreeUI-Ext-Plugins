Imports System
Imports System.Collections.Generic
Imports System.Text

Namespace videoenhancer
    Friend Class CommandToken
        Friend Property Text As String
        Friend Property WasQuoted As Boolean
    End Class
    Friend NotInheritable Class CommandLineTokenizer
        Friend Shared Function Tokenize(line As String) As List(Of CommandToken)
            Dim tokens As New List(Of CommandToken)
            Dim sb As New StringBuilder()
            Dim inQuotes As Boolean = False
            Dim tokenQuoted As Boolean = False
            For Each c As Char In line
                If c = """"c Then
                    inQuotes = Not inQuotes
                    tokenQuoted = True
                ElseIf Char.IsWhiteSpace(c) AndAlso Not inQuotes Then
                    If sb.Length > 0 Then
                        tokens.Add(New CommandToken With {.Text = sb.ToString(), .WasQuoted = tokenQuoted})
                        sb.Clear()
                        tokenQuoted = False
                    End If
                Else
                    sb.Append(c)
                End If
            Next
            If sb.Length > 0 Then
                tokens.Add(New CommandToken With {.Text = sb.ToString(), .WasQuoted = tokenQuoted})
            End If
            Return tokens
        End Function
    End Class
End Namespace
